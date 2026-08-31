using System.Collections;
using MiHordeFlow.Pathing;
using UnityEngine;
using UnityEngine.AI;

namespace MiHordeFlow.Movement
{
    /*
     * The chase behaviour a horde needs and nothing else: walk at a target, repath now and then, and be able to stop dead while an attack or a stagger plays out.
     * It owns the pathfinding and leaves crowd spacing entirely to NavMeshSeparationAgent, so neither has to know what the other is doing.
     *
     * Repathing is on an interval rather than every frame because SetDestination queues a path request,
     * and a thousand agents asking every frame is a thousand requests a frame for a target that has moved a few centimetres.
     * The countdown starts at a random point in its own interval so a wave spawned in one frame does not then repath in lockstep forever,
     * which turns a smooth cost into a spike every interval.
     *
     * Freezing sets isStopped rather than disabling the agent. A disabled agent leaves the navmesh and has to be warped back,
     * and anything that was standing on it during those frames pathed through where it used to be.
     * isStopped keeps the agent in place and on the mesh, and stops the steering and path following that make up most of what an idle agent costs.
     */
    /// <summary>
    /// Chases a target with NavMeshAgent pathfinding, with freeze and resume for when something else takes over.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    [DisallowMultipleComponent]
    public class HordeEntity : MonoBehaviour, IRepathBody
    {

        [SerializeField] private NavMeshAgent agent;
        [SerializeField] private NavMeshSeparationAgent separation;

        [Header("Pathfinding")]
        [SerializeField, Min(0f)] private float repathInterval = .1f;
        [SerializeField, Min(0f)] private float repathThreshold = .1f;
        [SerializeField] private bool staggerFirstRepath = true;

        [Header("Freezing")]
        [SerializeField] private bool separateWhileFrozen = true;

        private Transform _target;
        private Vector3 _requestedDestination = Vector3.positiveInfinity;
        private float _repathCountdown;
        private bool _frozen;
        private int _repathIndex = -1;
        private float _nextRepathTime;
        private float _lastRepathTime;

        /// <summary>
        /// Slot this entity occupies in the scheduler's roster. Written by the HordePathScheduler only.
        /// </summary>
        public int RepathIndex
        {
            get => _repathIndex;
            set => _repathIndex = value;
        }

        /// <summary>
        /// When this entity is next due a repath. Written by the HordePathScheduler only.
        /// </summary>
        public float NextRepathTime
        {
            get => _nextRepathTime;
            set => _nextRepathTime = value;
        }

        /// <summary>
        /// When this entity was last repathed. Written by the HordePathScheduler only.
        /// </summary>
        public float LastRepathTime
        {
            get => _lastRepathTime;
            set => _lastRepathTime = value;
        }

        /// <summary>
        /// Whether repathing this entity would mean anything right now.
        /// </summary>
        public bool WantsRepath => !_frozen && _target && agent && agent.enabled;

        /// <summary>
        /// The agent doing the pathfinding.
        /// </summary>
        public NavMeshAgent Agent => agent;

        /// <summary>
        /// The separation component this entity leaves crowd spacing to, which may be null.
        /// </summary>
        public NavMeshSeparationAgent Separation => separation;

        /// <summary>
        /// Who this entity is walking towards, or null when it is heading to a fixed point or nowhere.
        /// </summary>
        public Transform Target => _target;

        /// <summary>
        /// Whether movement and repathing are currently suspended.
        /// </summary>
        public bool IsFrozen => _frozen;

        /// <summary>
        /// Sets who this entity chases. It repaths on the next frame rather than waiting out the current interval.
        /// </summary>
        /// <param name="target">The transform to follow, or null to stop chasing.</param>
        public void SetTarget(Transform target)
        {
            _target = target;
            _requestedDestination = Vector3.positiveInfinity;
            _repathCountdown = 0f;
        }

        /// <summary>
        /// Sends this entity to a fixed point instead of a moving target, which clears whatever it was chasing.
        /// </summary>
        /// <param name="position">Where to walk to.</param>
        public void SetDestination(Vector3 position)
        {
            _target = null;
            _requestedDestination = position;

            if (CanPath()) agent.SetDestination(position);
        }

        /// <summary>
        /// Stops chasing and clears the current path, leaving the entity standing where it is.
        /// </summary>
        public void ClearTarget()
        {
            _target = null;
            _requestedDestination = Vector3.positiveInfinity;

            if (CanPath()) agent.ResetPath();
        }

        /// <summary>
        /// Repaths immediately, ignoring both the interval and the distance threshold.
        /// </summary>
        public void ForceRepath()
        {
            _requestedDestination = Vector3.positiveInfinity;
            _repathCountdown = repathInterval;
            _nextRepathTime = Time.time;
            Repath(Time.time);
        }

        /// <summary>
        /// Suspends movement and repathing, for an attack, a stagger or anything else that takes over the entity.
        /// The agent stays on the navmesh and keeps its place in the crowd.
        /// </summary>
        public void Freeze()
        {
            if (_frozen) return;

            _frozen = true;

            if (CanPath()) agent.isStopped = true;
            if (!separateWhileFrozen && separation) separation.SetSeparationEnabled(false);
        }

        /// <summary>
        /// Resumes movement and repathing, pathing again on the next frame since the world moved while it was frozen.
        /// </summary>
        public void Resume()
        {
            if (!_frozen) return;

            _frozen = false;

            if (CanPath()) agent.isStopped = false;
            if (!separateWhileFrozen && separation) separation.SetSeparationEnabled(true);

            if (_target) _requestedDestination = Vector3.positiveInfinity;
            _repathCountdown = 0f;
        }

        /// <summary>
        /// Freezes or resumes in one call, for driving from a state machine.
        /// </summary>
        /// <param name="value">True to freeze, false to resume.</param>
        public void SetFrozen(bool value)
        {
            if (value) Freeze();
            else Resume();
        }

        /// <summary>
        /// Moves the entity without letting the agent try to path there.
        /// </summary>
        /// <param name="position">Where to land, which must already be on the navmesh.</param>
        public void Warp(Vector3 position)
        {
            agent.Warp(position);
            _requestedDestination = Vector3.positiveInfinity;
        }

        /// <summary>
        /// Warps at the end of the frame, for a pooled entity that is not on the navmesh yet at the moment it is spawned.
        /// </summary>
        /// <param name="position">Where to land.</param>
        public void TeleportAgent(Vector3 position)
        {
            StopAllCoroutines();
            StartCoroutine(WarpAtEndOfFrame(position));
        }

        private void Reset()
        {
            agent = GetComponent<NavMeshAgent>();
            separation = GetComponent<NavMeshSeparationAgent>();
        }

        private void Awake()
        {
            agent = agent ? agent : GetComponent<NavMeshAgent>();
            separation = separation ? separation : GetComponent<NavMeshSeparationAgent>();
        }

        private void OnEnable()
        {
            _frozen = false;
            _requestedDestination = Vector3.positiveInfinity;
            _repathCountdown = staggerFirstRepath ? Random.value * repathInterval : 0f;

            HordePathScheduler.Register(this);
        }

        private void OnDisable() => HordePathScheduler.Unregister(this);

        /*
         * Skipped entirely when a scheduler is present, rather than running alongside it. Two things deciding when
         * an entity repaths means the interval that actually applies is the shorter of the two, which makes the
         * scheduler's budget a suggestion and its distance pacing invisible.
         */
        private void Update()
        {
            if (HordePathScheduler.Instance) return;
            if (_frozen || !agent.enabled) return;

            _repathCountdown -= Time.deltaTime;
            if (_repathCountdown > 0f) return;

            _repathCountdown = repathInterval;
            Repath(Time.time);
        }

        private bool CanPath() => agent && agent.enabled && agent.isOnNavMesh;

        /// <summary>
        /// Recomputes the path. Called by the HordePathScheduler when this entity's turn comes up,
        /// or by this entity's own interval when there is no scheduler in the scene.
        /// </summary>
        /// <param name="time">The current time.</param>
        /// <returns>Distance to the goal, which is what the scheduler turns into how soon to come back.</returns>
        public float Repath(float time)
        {
            if (!_target || !CanPath()) return float.MaxValue;

            Vector3 destination = _target.position;
            float distance = Vector3.Distance(destination, transform.position);

            if (Vector3.Distance(destination, _requestedDestination) > repathThreshold)
            {
                _requestedDestination = destination;
                agent.SetDestination(destination);
            }

            return distance;
        }

        private IEnumerator WarpAtEndOfFrame(Vector3 position)
        {
            yield return new WaitForEndOfFrame();
            Warp(position);
        }

    }
}
