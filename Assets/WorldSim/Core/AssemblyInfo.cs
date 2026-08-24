using System.Runtime.CompilerServices;

// The MQTT topic-filter matcher and the covering set it feeds are internal on
// purpose - nothing outside Core should be picking filters apart. The EditMode
// tests do need them, because they guard a property whose failure mode is silent
// total message loss: the covering set is never empty for a non-empty input.
[assembly: InternalsVisibleTo("WorldSim.Core.Tests")]
