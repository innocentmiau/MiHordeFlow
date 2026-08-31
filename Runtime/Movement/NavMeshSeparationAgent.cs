using MiHordeFlow.Separation;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.AI;

namespace MiHordeFlow.Movement
{
    /*
     * Drop this next to a NavMeshAgent and nothing else has to change.
     * It does not replace, wrap or subclass whatever behaviour already drives the agent's destination, it only turns the built in avoidance off
     * and feeds the separation push back in, so existing enemy scripts keep working untouched.
     *
     * The radius is its own serialized value rather than a read of NavMeshAgent.radius, because that radius is also the clearance the pathfinder keeps from walls.
     * Shrinking it to let a horde pack tighter would quietly change how everything paths around corners.
     * Reset and the context menu seed it from the agent, so the default matches and only a deliberate change diverges.
     */
    /// <summary>
    /// Makes a NavMeshAgent take part in the SeparationSystem instead of using its own local avoidance.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    [DisallowMultipleComponent]
    public class NavMeshSeparationAgent : MonoBehaviour, ISeparationBody
    {

        [SerializeField] private NavMeshAgent agent;
        [SerializeField] private SeparationSettings group;
        [SerializeField] private float separationRadius = .5f;
        [SerializeField] private bool disableBuiltInAvoidance = true;
        [SerializeField] private SeparationApplyMode applyMode = SeparationApplyMode.NAVMESH_MOVE;

        private int _separationIndex = -1;
        private bool _separationEnabled = true;

        /// <summary>
        /// Slot this agent occupies in its group's arrays. Written by the SeparationSystem only.
        /// </summary>
        public int SeparationIndex
        {
            get => _separationIndex;
            set => _separationIndex = value;
        }

        /// <summary>
        /// The settings asset naming which crowd this agent separates within, or null for the system's default.
        /// </summary>
        public SeparationSettings GroupSettings => group;

        /// <summary>
        /// How much room this agent wants around itself, independent of the navmesh radius used for path clearance.
        /// </summary>
        public float SeparationRadius => separationRadius;

        /// <summary>
        /// The agent's current world position.
        /// </summary>
        public float3 SeparationPosition => transform.position;

        /// <summary>
        /// Whether this agent is currently taking part in separation at all.
        /// </summary>
        public bool SeparationEnabled => _separationEnabled;

        /// <summary>
        /// Applies the push the system computed for this agent, projected back onto the navmesh.
        /// </summary>
        /// <param name="push">Push velocity in world units per second.</param>
        /// <param name="deltaTime">Frame time to scale the push by.</param>
        public void ApplySeparationPush(float3 push, float deltaTime) => agent.ApplySeparationPush(push, deltaTime, applyMode);

        /// <summary>
        /// Drops this agent out of separation entirely, or puts it back in. An agent that is out is not sampled,
        /// not solved against and not pushed, which is the cheapest state for something standing still.
        /// </summary>
        /// <param name="value">True to take part, false to drop out.</param>
        public void SetSeparationEnabled(bool value)
        {
            if (_separationEnabled == value) return;

            _separationEnabled = value;

            if (!isActiveAndEnabled) return;

            if (value) SeparationSystem.Register(this);
            else SeparationSystem.Unregister(this);
        }

        /// <summary>
        /// Moves this agent into a different crowd, re-registering it so the change takes effect this frame.
        /// </summary>
        /// <param name="value">The settings asset naming the group to join, or null for the system's default.</param>
        public void SetGroup(SeparationSettings value)
        {
            if (group == value) return;

            bool wasRegistered = _separationEnabled && isActiveAndEnabled;

            if (wasRegistered) SeparationSystem.Unregister(this);

            group = value;

            if (wasRegistered) SeparationSystem.Register(this);
        }

        /// <summary>
        /// Switches the hand off at runtime, so a benchmark can compare them without respawning the crowd.
        /// </summary>
        /// <param name="value">The hand off to use from now on.</param>
        public void SetApplyMode(SeparationApplyMode value) => applyMode = value;

        [ContextMenu("Match Radius To Agent")]
        private void MatchRadiusToAgent()
        {
            agent = agent ? agent : GetComponent<NavMeshAgent>();
            separationRadius = agent ? agent.radius : separationRadius;
        }

        private void Reset() => MatchRadiusToAgent();

        private void OnEnable()
        {
            agent = agent ? agent : GetComponent<NavMeshAgent>();

            if (disableBuiltInAvoidance) agent.DisableBuiltInAvoidance();

            if (_separationEnabled) SeparationSystem.Register(this);
        }

        private void OnDisable()
        {
            if (_separationEnabled) SeparationSystem.Unregister(this);
        }

    }
}
