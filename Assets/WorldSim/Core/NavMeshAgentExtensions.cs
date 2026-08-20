using UnityEngine.AI;

namespace WorldSim.Core
{
    /// <summary>Shared NavMeshAgent helpers used across scenarios (public, not
    /// internal - CatBasement is a separate asmdef and needs to see this).</summary>
    public static class NavMeshAgentExtensions
    {
        /// <summary>True once the agent has a resolved path and has closed the
        /// distance to its destination. Centralizes the pathPending/remainingDistance/
        /// stoppingDistance check that every "did I get there yet" call site needs.</summary>
        public static bool HasArrived(this NavMeshAgent a) => !a.pathPending && a.remainingDistance <= a.stoppingDistance;
    }
}
