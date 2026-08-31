using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

namespace MiHordeFlow.Pathing
{
    /*
     * Repathing a horde every frame is wasted work and repathing it on one shared interval is wasted in a worse way:
     * the agents about to reach the player and the agents still crossing the map get the same share of a budget that
     * only the first group can do anything with.
     *
     * Priority here is not a sort. Each body's interval is a function of how far it is from its goal, so a body ten
     * metres out repaths ten times as often as one sixty metres out without anything ever ranking them against each
     * other. Sorting would only change which bodies win a single frame that ran out of budget, and rotating where
     * the scan starts spreads that around more evenly than a sort would anyway.
     *
     * The guarantee pass is separate and runs first. Distance based pacing alone can starve a body indefinitely if
     * the near ranks always fill the budget, and a body that never repaths is a body walking at where the player
     * used to be. Anything past the guarantee interval jumps the queue regardless of how far away it is.
     *
     * Both passes are managed field reads and float compares over the roster. That is a few microseconds at a few
     * thousand bodies, and it buys the right to spend the expensive part, which is the path request itself,
     * only on the bodies that are worth it.
     */
    /// <summary>
    /// Decides which bodies repath each frame, favouring the ones nearest their goal without starving the rest.
    /// </summary>
    [DisallowMultipleComponent]
    public class HordePathScheduler : MonoBehaviour
    {

        private static readonly ProfilerMarker SCHEDULE_MARKER = new ProfilerMarker("MiHordeFlow.Pathing.Schedule");

        /*
         * Registration is queued when no scheduler exists yet, because a body's OnEnable can easily run before the
         * scheduler's Awake, whether from script execution order or from a body that was already in the scene.
         * Dropping those on the floor meant a body silently never repathed for the rest of its life, which looks
         * exactly like the scheduler working and choosing not to.
         */
        private static readonly List<IRepathBody> PENDING = new List<IRepathBody>();

        private static HordePathScheduler _instance;

        /// <summary>
        /// The scheduler in the loaded scene, or null when there is none, in which case bodies pace themselves.
        /// </summary>
        public static HordePathScheduler Instance => _instance;

        [Header("Budget")]
        [SerializeField, Min(1)] private int repathsPerFrame = 100;

        [Header("Pacing")]
        [SerializeField, Min(0f)] private float nearInterval = .1f;
        [SerializeField, Min(0f)] private float farInterval = 1.5f;
        [SerializeField, Min(0f)] private float nearDistance = 10f;
        [SerializeField, Min(0f)] private float farDistance = 60f;

        [Header("Starvation")]
        [SerializeField, Min(0f)] private float guaranteedInterval = 4f;

        private readonly List<IRepathBody> _bodies = new List<IRepathBody>();

        private int _cursor;
        private int _repathsLastFrame;

        /// <summary>
        /// How many bodies the scheduler is pacing.
        /// </summary>
        public int BodyCount => _bodies.Count;

        /// <summary>
        /// How many repaths actually went out on the last frame, which is what to watch when tuning the budget.
        /// A number pinned at the budget every frame means the crowd is asking for more than it is being given.
        /// </summary>
        public int RepathsLastFrame => _repathsLastFrame;

        /// <summary>
        /// Puts a body under the scheduler's control. Safe from OnEnable.
        /// </summary>
        /// <param name="body">The body to pace.</param>
        public static void Register(IRepathBody body)
        {
            if (body.RepathIndex >= 0) return;

            if (!_instance)
            {
                if (!PENDING.Contains(body)) PENDING.Add(body);
                return;
            }

            _instance.AddBody(body);
        }

        /// <summary>
        /// Takes a body back off the scheduler.
        /// </summary>
        /// <param name="body">The body to stop pacing.</param>
        public static void Unregister(IRepathBody body)
        {
            PENDING.Remove(body);

            if (!_instance) return;

            int index = body.RepathIndex;
            List<IRepathBody> bodies = _instance._bodies;

            if (index < 0 || index >= bodies.Count || bodies[index] != body) return;

            int last = bodies.Count - 1;
            bodies[index] = bodies[last];
            bodies[index].RepathIndex = index;
            bodies.RemoveAt(last);
            body.RepathIndex = -1;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _instance = null;
            PENDING.Clear();
        }

        private void Awake()
        {
            if (_instance && _instance != this)
            {
                Destroy(this);
                return;
            }

            _instance = this;

            for (int i = 0; i < PENDING.Count; i++)
                AddBody(PENDING[i]);

            PENDING.Clear();
        }

        /*
         * Seeded at the current time rather than at zero, so a body that registers mid game is not treated as
         * though it had been waiting since the scene loaded. Otherwise a spawn wave arrives already past the
         * guarantee interval and blows a whole frame's budget on agents that have never had a path to begin with.
         */
        private void AddBody(IRepathBody body)
        {
            if (body.RepathIndex >= 0) return;

            float now = Time.time;

            body.RepathIndex = _bodies.Count;
            body.LastRepathTime = now;
            body.NextRepathTime = now;

            _bodies.Add(body);
        }

        private void OnDestroy()
        {
            if (_instance != this) return;

            for (int i = 0; i < _bodies.Count; i++)
                _bodies[i].RepathIndex = -1;

            _bodies.Clear();
            _instance = null;
        }

        private void Update()
        {
            int count = _bodies.Count;
            if (count == 0) return;

            float now = Time.time;
            int remaining = repathsPerFrame;

            using (SCHEDULE_MARKER.Auto())
            {
                remaining = RunPass(now, remaining, count, true);
                remaining = RunPass(now, remaining, count, false);
            }

            _repathsLastFrame = repathsPerFrame - remaining;

            /*
             * Advanced by a whole budget rather than to wherever the scan stopped, so the window moves across the
             * roster at a steady rate instead of parking on whichever stretch happened to be due. Without it the
             * same bodies sit at the front of the scan every frame and the far half of the roster only ever gets
             * looked at through the guarantee pass.
             */
            _cursor = count == 0 ? 0 : (_cursor + repathsPerFrame) % count;
        }

        /// <summary>
        /// One sweep of the roster, taking either the bodies past the starvation guarantee or the ones merely due.
        /// </summary>
        /// <param name="now">Current time.</param>
        /// <param name="remaining">How much of this frame's budget is left.</param>
        /// <param name="count">Roster size.</param>
        /// <param name="guaranteed">True to take only bodies past the guarantee interval, false to take due ones.</param>
        /// <returns>The budget left after this pass.</returns>
        private int RunPass(float now, int remaining, int count, bool guaranteed)
        {
            if (remaining <= 0) return 0;

            for (int i = 0; i < count; i++)
            {
                IRepathBody body = _bodies[(_cursor + i) % count];

                if (!body.WantsRepath) continue;

                bool due = guaranteed ? now - body.LastRepathTime >= guaranteedInterval : now >= body.NextRepathTime;
                if (!due) continue;

                float distance = body.Repath(now);

                body.LastRepathTime = now;
                body.NextRepathTime = now + IntervalFor(distance);

                if (--remaining <= 0) return 0;
            }

            return remaining;
        }

        /// <summary>
        /// How long a body that far from its goal should wait before its next repath.
        /// </summary>
        /// <param name="distance">Distance from the body to its goal.</param>
        /// <returns>Seconds until this body is due again.</returns>
        private float IntervalFor(float distance)
        {
            if (farDistance <= nearDistance) return nearInterval;

            float t = Mathf.Clamp01((distance - nearDistance) / (farDistance - nearDistance));
            return Mathf.Lerp(nearInterval, farInterval, t);
        }

    }
}
