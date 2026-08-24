using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using WorldSim.Core;

namespace WorldSim.Tests
{
    /// <summary>
    /// Guards MqttBridge's topic-filter matcher and the covering set built from it.
    ///
    /// This is the code that stops one deterrent command producing two acks: a broker
    /// delivers one copy of a PUBLISH per matching subscription, so two overlapping
    /// filters on one client double every message. MqttBridge keeps only the filters
    /// no other filter covers.
    ///
    /// Both failure directions are real. Too little coverage and a listener stops
    /// hearing its topic; too much and the covering set collapses to nothing and the
    /// client goes silent. The tests below pin each direction separately.
    /// </summary>
    public class MqttTopicFilterTests
    {
        // ---- Covers: the wildcard rules, MQTT 3.1.1 section 4.7 ----

        [TestCase("sport/#", "sport", true, TestName = "hash covers the parent level itself")]
        [TestCase("sport/#", "sport/a", true, TestName = "hash covers one level below")]
        [TestCase("sport/#", "sport/a/b", true, TestName = "hash covers arbitrary depth")]
        [TestCase("sport/#", "other/a", false, TestName = "hash does not cross a literal prefix")]
        [TestCase("#", "sport/a/b", true, TestName = "bare hash covers everything")]
        [TestCase("sport/+", "sport/a", true, TestName = "plus covers exactly one level")]
        [TestCase("sport/+", "sport/a/b", false, TestName = "plus does not cover two levels")]
        [TestCase("sport/+", "sport", false, TestName = "plus does not cover a missing level")]
        [TestCase("sport/a", "sport/+", false, TestName = "a literal never covers a plus")]
        [TestCase("sport/a", "sport/#", false, TestName = "a literal never covers a hash")]
        [TestCase("sport/+", "sport/#", false, TestName = "plus never covers a hash")]
        [TestCase("sport/a", "sport/a", true, TestName = "a filter covers itself")]
        [TestCase("sport/+/b", "sport/x/b", true, TestName = "plus matches mid-filter")]
        [TestCase("sport/+/b", "sport/x/c", false, TestName = "levels after a plus still have to match")]
        public void CoversFollowsTheWildcardRules(string broad, string narrow, bool expected)
        {
            Assert.AreEqual(expected, MqttBridge.Covers(broad, narrow));
        }

        [Test]
        public void WildcardsDoNotAbsorbTheBrokersOwnTree()
        {
            // Section 4.7.2: a leading wildcard never matches a $-prefixed topic, so
            // it cannot stand in for a filter that names one.
            Assert.IsFalse(MqttBridge.Covers("#", "$SYS/a"));
            Assert.IsFalse(MqttBridge.Covers("+/a", "$SYS/a"));
            Assert.IsTrue(MqttBridge.Covers("$SYS/#", "$SYS/a"));
        }

        [Test]
        public void CoversTheFilterPairThatCausedTheDuplicateAckBug()
        {
            // EventFeed subscribes the first, VirtualTurret the second, both through
            // one MqttBridge. Before the covering set both went on the wire and every
            // fire command arrived twice.
            Assert.IsTrue(MqttBridge.Covers("catsentry/#", "catsentry/deterrent/fire"));
            Assert.IsFalse(MqttBridge.Covers("catsentry/deterrent/fire", "catsentry/#"));
        }

        [Test]
        public void PlusHashIsAbsorbedByHashAndNotTheOtherWayAround()
        {
            // "#" and "+/#" match exactly the same topics, so a matcher that answered
            // by set inclusion alone would say each covers the other. CoveringSet
            // would then drop BOTH and the client would hold no subscriptions at all.
            //
            // Covers is deliberately stricter than set inclusion here so the tie
            // breaks one way: "#" survives, "+/#" is absorbed. This asymmetry is a
            // safety property, not an approximation. Do not "fix" it.
            Assert.IsTrue(MqttBridge.Covers("#", "+/#"));
            Assert.IsFalse(MqttBridge.Covers("+/#", "#"));
        }

        [Test]
        public void NoTwoDistinctFiltersCoverEachOther()
        {
            // CoveringSet drops any filter another filter covers. If coverage were
            // ever mutual for two distinct filters it would drop both, so this
            // property is what keeps the covering set non-empty.
            string[] filters =
            {
                "#", "+", "+/#", "+/+", "a", "a/#", "a/+", "a/b", "a/b/#", "a/+/c",
                "a/b/c", "$SYS", "$SYS/#", "catsentry/#", "catsentry/deterrent/fire"
            };

            foreach (string x in filters)
            foreach (string y in filters)
            {
                if (x == y) continue;
                Assert.IsFalse(
                    MqttBridge.Covers(x, y) && MqttBridge.Covers(y, x),
                    "\"" + x + "\" and \"" + y + "\" cover each other, so both would be dropped");
            }
        }

        // ---- CoveringSet: what actually reaches the broker ----

        MqttBridge _bridge;

        [SetUp]
        public void SetUp()
        {
            // Built on an inactive GameObject so neither Awake nor OnEnable runs -
            // OnEnable would try to open a socket, and none of this needs a broker.
            var go = new GameObject("MqttTopicFilterTests");
            go.SetActive(false);
            _bridge = go.AddComponent<MqttBridge>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_bridge != null) Object.DestroyImmediate(_bridge.gameObject);
        }

        [Test]
        public void CoveringSetKeepsOnlyTheBroadFilter()
        {
            _bridge.Subscribe("catsentry/#", MqttBridge.QosAtLeastOnce);
            _bridge.Subscribe("catsentry/deterrent/fire", MqttBridge.QosAtLeastOnce);

            Dictionary<string, byte> covering = _bridge.CoveringSet();

            Assert.AreEqual(1, covering.Count, "the narrow filter should have been absorbed");
            Assert.IsTrue(covering.ContainsKey("catsentry/#"));
        }

        [Test]
        public void CoveringSetIsTheSameWhicheverOrderFiltersArriveIn()
        {
            // Either component may reach OnEnable first and Unity does not specify
            // which, so registration order must not change what goes on the wire.
            _bridge.Subscribe("catsentry/deterrent/fire", MqttBridge.QosAtLeastOnce);
            _bridge.Subscribe("catsentry/#", MqttBridge.QosAtLeastOnce);

            Dictionary<string, byte> covering = _bridge.CoveringSet();

            Assert.AreEqual(1, covering.Count);
            Assert.IsTrue(covering.ContainsKey("catsentry/#"));
        }

        [Test]
        public void ACoveringFilterCarriesTheStrongestQosItAbsorbs()
        {
            // Folding a QoS 1 filter under a QoS 0 one must not quietly downgrade it.
            _bridge.Subscribe("catsentry/#", MqttBridge.QosAtMostOnce);
            _bridge.Subscribe("catsentry/deterrent/fire", MqttBridge.QosAtLeastOnce);

            Dictionary<string, byte> covering = _bridge.CoveringSet();

            Assert.AreEqual(1, covering.Count);
            Assert.AreEqual(MqttBridge.QosAtLeastOnce, covering["catsentry/#"]);
        }

        [Test]
        public void UnrelatedFiltersAreAllKept()
        {
            _bridge.Subscribe("catsentry/deterrent/fire", MqttBridge.QosAtLeastOnce);
            _bridge.Subscribe("other/telemetry", MqttBridge.QosAtMostOnce);

            Assert.AreEqual(2, _bridge.CoveringSet().Count);
        }

        [Test]
        public void CoveringSetIsNeverEmptyForANonEmptyInput()
        {
            // The failure this guards against is silent: an empty covering set means
            // no wire subscriptions, so nothing is delivered and nothing errors.
            string[] filters =
            {
                "#", "+", "+/#", "+/+", "a", "a/#", "a/+", "a/b", "a/b/#", "a/+/c",
                "$SYS/#", "catsentry/#", "catsentry/deterrent/fire", "catsentry/telemetry"
            };

            foreach (string filter in filters)
            {
                _bridge.Subscribe(filter, MqttBridge.QosAtLeastOnce);
                Assert.Greater(
                    _bridge.CoveringSet().Count, 0,
                    "covering set went empty after adding \"" + filter + "\"");
            }
        }
    }
}
