using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace WorldSim.Core
{
    /// <summary>
    /// Minimal MQTT 3.1.1 client: CONNECT, SUBSCRIBE, PUBLISH (QoS 0/1), PINGREQ.
    /// Enough for catsentry-v1 and nothing more.
    ///
    /// ponytail: this replaces M2Mqtt, which ships no netstandard target - its
    /// net45 assembly only compiles when the project's API Compatibility Level is
    /// flipped to .NET Framework, and that setting can't be applied before Unity's
    /// first script compile (chicken-and-egg on a fresh clone). docs/design.md
    /// leaves the MQTT client to the implementer, "smallest working option wins";
    /// ~200 lines with zero dependencies beats a DLL that constrains the whole
    /// project's runtime profile. Same reasoning as the hand-rolled MJPEG server.
    ///
    /// Threading: one background receive thread. All callbacks fire on that
    /// thread - callers marshal to the main thread themselves (MqttBridge does).
    /// </summary>
    public class MqttClientLite
    {
        const byte PacketConnect = 0x10;
        const byte PacketConnack = 0x20;
        const byte PacketPublish = 0x30;
        const byte PacketPuback = 0x40;
        const byte PacketSubscribe = 0x82;
        const byte PacketSuback = 0x90;
        const byte PacketUnsubscribe = 0xA2;
        const byte PacketPingreq = 0xC0;
        const byte PacketDisconnect = 0xE0;

        readonly object _writeLock = new object();

        TcpClient _tcp;
        NetworkStream _stream;
        Thread _receiveThread;
        volatile bool _running;
        ushort _packetId;

        /// <summary>(topic, payload) on the receive thread.</summary>
        public event Action<string, string> MessageReceived;

        /// <summary>Fired when the socket drops after a successful connect.</summary>
        public event Action ConnectionClosed;

        public bool IsConnected { get; private set; }

        /// <summary>Blocking connect + CONNACK wait. Throws on failure - callers
        /// run this off the main thread.</summary>
        public void Connect(string host, int port, string clientId, ushort keepAliveSec = 30)
        {
            _tcp = new TcpClient();
            _tcp.Connect(host, port);
            _tcp.NoDelay = true;
            _stream = _tcp.GetStream();

            byte[] id = Encoding.UTF8.GetBytes(clientId);
            var variable = new List<byte>();
            variable.AddRange(EncodeString("MQTT"));
            variable.Add(4);    // protocol level 3.1.1
            variable.Add(0x02); // clean session
            variable.Add((byte)(keepAliveSec >> 8));
            variable.Add((byte)(keepAliveSec & 0xFF));
            variable.AddRange(EncodeBytes(id));
            WritePacket(PacketConnect, variable.ToArray());

            _stream.ReadTimeout = 5000;
            if (!ReadPacket(out byte header, out byte[] body) || (header & 0xF0) != PacketConnack)
                throw new IOException_("no CONNACK from broker");
            if (body.Length < 2 || body[1] != 0)
                throw new IOException_("broker refused connection, code " + (body.Length > 1 ? body[1] : (byte)255));

            _stream.ReadTimeout = Timeout.Infinite;
            IsConnected = true;
            _running = true;
            _receiveThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "MqttClientLite.Receive" };
            _receiveThread.Start();
        }

        public void Subscribe(string topic, byte qos)
        {
            var body = new List<byte>();
            ushort pid = NextPacketId();
            body.Add((byte)(pid >> 8));
            body.Add((byte)(pid & 0xFF));
            body.AddRange(EncodeString(topic));
            body.Add(qos);
            WritePacket(PacketSubscribe, body.ToArray());
        }

        /// <summary>Drop one or more subscriptions. UNSUBSCRIBE carries a packet id
        /// and then the filters as bare length-prefixed strings - no QoS byte, unlike
        /// SUBSCRIBE. The UNSUBACK is swallowed by the receive loop with the other
        /// acknowledgements.</summary>
        public void Unsubscribe(IList<string> topics)
        {
            if (topics == null || topics.Count == 0) return;

            var body = new List<byte>();
            ushort pid = NextPacketId();
            body.Add((byte)(pid >> 8));
            body.Add((byte)(pid & 0xFF));
            for (int i = 0; i < topics.Count; i++)
                body.AddRange(EncodeString(topics[i]));
            WritePacket(PacketUnsubscribe, body.ToArray());
        }

        public void Publish(string topic, string payload, byte qos)
        {
            var body = new List<byte>();
            body.AddRange(EncodeString(topic));
            if (qos > 0)
            {
                ushort pid = NextPacketId();
                body.Add((byte)(pid >> 8));
                body.Add((byte)(pid & 0xFF));
            }
            body.AddRange(Encoding.UTF8.GetBytes(payload));
            WritePacket((byte)(PacketPublish | (qos << 1)), body.ToArray());
        }

        public void Ping()
        {
            WritePacket(PacketPingreq, Array.Empty<byte>());
        }

        public void Disconnect()
        {
            _running = false;
            if (IsConnected)
            {
                try { WritePacket(PacketDisconnect, Array.Empty<byte>()); }
                catch (Exception) { /* socket already gone */ }
            }
            IsConnected = false;
            try { _stream?.Close(); } catch (Exception) { }
            try { _tcp?.Close(); } catch (Exception) { }
            _stream = null;
            _tcp = null;
        }

        // ---- receive ----

        void ReceiveLoop()
        {
            try
            {
                while (_running)
                {
                    if (!ReadPacket(out byte header, out byte[] body)) break;

                    if ((header & 0xF0) != PacketPublish) continue; // CONNACK/SUBACK/PUBACK/PINGRESP: nothing to do

                    int idx = 0;
                    string topic = DecodeString(body, ref idx);
                    byte qos = (byte)((header >> 1) & 0x03);
                    ushort pid = 0;
                    if (qos > 0)
                    {
                        pid = (ushort)((body[idx] << 8) | body[idx + 1]);
                        idx += 2;
                    }
                    string payload = Encoding.UTF8.GetString(body, idx, body.Length - idx);

                    if (qos == 1)
                    {
                        // PUBACK so the broker stops redelivering.
                        WritePacket(PacketPuback, new[] { (byte)(pid >> 8), (byte)(pid & 0xFF) });
                    }

                    MessageReceived?.Invoke(topic, payload);
                }
            }
            catch (Exception)
            {
                // socket closed or malformed stream - fall through to the notify below
            }

            bool wasConnected = IsConnected;
            IsConnected = false;
            _running = false;
            if (wasConnected) ConnectionClosed?.Invoke();
        }

        bool ReadPacket(out byte header, out byte[] body)
        {
            header = 0;
            body = null;

            int first = _stream.ReadByte();
            if (first < 0) return false;
            header = (byte)first;

            int multiplier = 1, length = 0;
            while (true)
            {
                int digit = _stream.ReadByte();
                if (digit < 0) return false;
                length += (digit & 127) * multiplier;
                if ((digit & 0x80) == 0) break;
                multiplier *= 128;
                if (multiplier > 128 * 128 * 128) return false; // malformed remaining-length
            }

            body = new byte[length];
            int read = 0;
            while (read < length)
            {
                int n = _stream.Read(body, read, length - read);
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }

        // ---- write ----

        void WritePacket(byte header, byte[] body)
        {
            lock (_writeLock)
            {
                NetworkStream stream = _stream;
                if (stream == null) throw new IOException_("not connected");
                stream.WriteByte(header);
                byte[] len = EncodeRemainingLength(body.Length);
                stream.Write(len, 0, len.Length);
                if (body.Length > 0) stream.Write(body, 0, body.Length);
                stream.Flush();
            }
        }

        ushort NextPacketId()
        {
            lock (_writeLock)
            {
                _packetId = (ushort)(_packetId == 65535 ? 1 : _packetId + 1);
                return _packetId;
            }
        }

        static byte[] EncodeRemainingLength(int value)
        {
            var bytes = new List<byte>(4);
            do
            {
                byte digit = (byte)(value % 128);
                value /= 128;
                if (value > 0) digit |= 0x80;
                bytes.Add(digit);
            } while (value > 0);
            return bytes.ToArray();
        }

        static byte[] EncodeString(string s) => EncodeBytes(Encoding.UTF8.GetBytes(s));

        static byte[] EncodeBytes(byte[] data)
        {
            var result = new byte[data.Length + 2];
            result[0] = (byte)(data.Length >> 8);
            result[1] = (byte)(data.Length & 0xFF);
            Buffer.BlockCopy(data, 0, result, 2, data.Length);
            return result;
        }

        static string DecodeString(byte[] buffer, ref int index)
        {
            int len = (buffer[index] << 8) | buffer[index + 1];
            index += 2;
            string s = Encoding.UTF8.GetString(buffer, index, len);
            index += len;
            return s;
        }

        /// <summary>Protocol-level failure. Named to avoid colliding with
        /// System.IO.IOException, which callers here never distinguish from it.</summary>
        public class IOException_ : Exception
        {
            public IOException_(string message) : base(message) { }
        }
    }
}
