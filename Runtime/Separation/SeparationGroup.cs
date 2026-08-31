using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;

namespace MiHordeFlow.Separation
{
    /*
     * One group is one independent crowd: its own roster, its own arrays, its own grid, its own job.
     * Bodies in different groups never see each other, which is the point. A flock of flyers wants FULL_3D and a tick every frame,
     * a herd of slow melee wants XZ and a tick every third frame, and folding both into one solve would mean picking settings that suit neither.
     * Running them apart also keeps each grid smaller, and a spatial hash gets cheaper faster than linearly as the population in it drops.
     *
     * The cost of that split is that a flyer and a walker standing in the same spot will not push each other out.
     * That is usually what you want for flyers, and where it is not, the answer is to put them in one group with a plane that suits both rather than to make groups aware of each other.
     *
     * Everything here runs from SeparationSystem and assumes that ordering:
     * nothing touches the arrays between a Schedule and the CompletePending that joins it.
     */
    /// <summary>
    /// One independent separation crowd, holding the roster and native buffers for every body that shares a settings asset.
    /// </summary>
    public class SeparationGroup
    {

        private const int MINIMUM_CAPACITY = 64;
        private const int BUILD_BATCH = 128;
        private const int PUSH_BATCH = 32;
        private const float MINIMUM_CELL_SIZE = .01f;

        /*
         * Named markers rather than raw timing, so the cost of this system shows up as its own row in the Profiler
         * next to the engine's own AI row. Total frame time cannot separate the two, which is exactly the comparison
         * anyone tuning this needs to make. All groups share the three markers, so the rows stay readable
         * however many groups a scene ends up with.
         */
        private static readonly ProfilerMarker COMPLETE_MARKER = new ProfilerMarker("MiHordeFlow.Separation.Complete");
        private static readonly ProfilerMarker APPLY_MARKER = new ProfilerMarker("MiHordeFlow.Separation.Apply");
        private static readonly ProfilerMarker TICK_MARKER = new ProfilerMarker("MiHordeFlow.Separation.Tick");

        private readonly SeparationSettings _settings;
        private readonly List<ISeparationBody> _bodies = new List<ISeparationBody>();

        private NativeArray<float3> _positionsFront;
        private NativeArray<float3> _positionsBack;
        private NativeArray<float> _radiiFront;
        private NativeArray<float> _radiiBack;
        private NativeArray<float3> _pushFront;
        private NativeArray<float3> _pushBack;
        private NativeParallelMultiHashMap<int, int> _grid;

        private JobHandle _handle;
        private bool _scheduled;
        private bool _pushReady;
        private bool _pushFresh;
        private int _capacity;
        private int _scheduledCount;
        private int _framesSinceTick;
        private float _largestRadius;

        /// <summary>
        /// The settings this group runs on, which is also what identifies it.
        /// </summary>
        public SeparationSettings Settings => _settings;

        /// <summary>
        /// How many bodies this group is separating, not counting anything queued this frame.
        /// </summary>
        public int BodyCount => _bodies.Count;

        /// <summary>
        /// Width of one grid cell as of the last tick, which is the configured size or the one derived from the largest radius.
        /// </summary>
        public float CellSize => _settings.CellSize > 0f ? _settings.CellSize : math.max(_largestRadius * 2f, MINIMUM_CELL_SIZE);

        /// <summary>
        /// Creates a group and sizes its buffers up front so a spawn wave does not reallocate them.
        /// </summary>
        /// <param name="settings">The settings this group runs on. Never null.</param>
        public SeparationGroup(SeparationSettings settings)
        {
            _settings = settings;
            EnsureCapacity(settings.InitialCapacity);
        }

        /// <summary>
        /// The plane mask that zeroes out whichever axes separation is not allowed to act on.
        /// </summary>
        /// <param name="plane">The plane to mask for.</param>
        /// <returns>A vector of ones on the active axes and zeroes elsewhere.</returns>
        public static float3 MaskFor(SeparationPlane plane) => plane switch
        {
            SeparationPlane.XY => new float3(1f, 1f, 0f),
            SeparationPlane.FULL_3D => new float3(1f, 1f, 1f),
            _ => new float3(1f, 0f, 1f)
        };

        /// <summary>
        /// Puts a body on the roster. Only ever called with no job in flight.
        /// </summary>
        /// <param name="body">The body to start separating.</param>
        public void Add(ISeparationBody body)
        {
            body.SeparationIndex = _bodies.Count;
            _bodies.Add(body);
        }

        /// <summary>
        /// Takes a body off the roster if it is on this one. Only ever called with no job in flight.
        /// </summary>
        /// <param name="body">The body to stop separating.</param>
        /// <returns>True when the body belonged to this group and was removed.</returns>
        public bool TryRemove(ISeparationBody body)
        {
            int index = body.SeparationIndex;
            if (index < 0 || index >= _bodies.Count || _bodies[index] != body) return false;

            int last = _bodies.Count - 1;
            _bodies[index] = _bodies[last];
            _bodies[index].SeparationIndex = index;
            _bodies.RemoveAt(last);
            body.SeparationIndex = -1;
            return true;
        }

        /// <summary>
        /// Joins whatever job this group has outstanding and hands the results to its bodies.
        /// </summary>
        /// <param name="deltaTime">Frame time to scale the pushes by.</param>
        public void CompleteAndApply(float deltaTime)
        {
            CompletePending();
            ApplyPushToBodies(deltaTime);
        }

        /// <summary>
        /// Samples the roster and schedules the next solve, if this group's interval says it is due.
        /// </summary>
        public void TickIfDue()
        {
            _framesSinceTick++;
            if (_framesSinceTick < _settings.UpdateInterval) return;

            _framesSinceTick = 0;

            using (TICK_MARKER.Auto())
            {
                EnsureCapacity(_bodies.Count);
                SampleBodies();
                Schedule();
            }
        }

        /// <summary>
        /// Joins the outstanding job at the end of the frame, for the completion modes that ask for it.
        /// </summary>
        public void TryCompleteInLateUpdate()
        {
            if (!_scheduled) return;

            if (_settings.CompletionMode == SeparationCompletionMode.LATE_UPDATE)
            {
                CompletePending();
                return;
            }

            if (_settings.CompletionMode == SeparationCompletionMode.POLLED && _handle.IsCompleted) CompletePending();
        }

        /// <summary>
        /// Joins any outstanding job and frees every native buffer this group owns.
        /// </summary>
        public void Dispose()
        {
            if (_scheduled)
            {
                _handle.Complete();
                _scheduled = false;
            }

            DisposeBuffers();
            _bodies.Clear();
            _capacity = 0;
        }

        /// <summary>
        /// Joins the outstanding job and promotes what it wrote into the buffer the bodies read from.
        /// </summary>
        private void CompletePending()
        {
            if (!_scheduled) return;

            using (COMPLETE_MARKER.Auto())
                _handle.Complete();

            _scheduled = false;

            (_pushFront, _pushBack) = (_pushBack, _pushFront);
            _pushReady = true;
            _pushFresh = true;
        }

        private void ApplyPushToBodies(float deltaTime)
        {
            if (!_pushReady) return;
            if (!_pushFresh && !_settings.ReusePushBetweenTicks) return;

            int count = math.min(_scheduledCount, _bodies.Count);

            using (APPLY_MARKER.Auto())
            {
                for (int i = 0; i < count; i++)
                    _bodies[i].ApplySeparationPush(_pushFront[i], deltaTime);
            }

            _pushFresh = false;
        }

        private void SampleBodies()
        {
            float largestRadius = 0f;

            for (int i = 0; i < _bodies.Count; i++)
            {
                ISeparationBody body = _bodies[i];
                float radius = body.SeparationRadius;
                _positionsBack[i] = body.SeparationPosition;
                _radiiBack[i] = radius;
                largestRadius = math.max(largestRadius, radius);
            }

            _largestRadius = largestRadius;
        }

        private void Schedule()
        {
            _scheduledCount = _bodies.Count;
            if (_scheduledCount == 0) return;

            float cellSize = CellSize;
            float3 planeMask = MaskFor(_settings.Plane);

            _grid.Clear();

            SpatialHashBuildJob buildJob = new SpatialHashBuildJob
            {
                Positions = _positionsBack,
                CellSize = cellSize,
                PlaneMask = planeMask,
                Grid = _grid.AsParallelWriter()
            };

            SeparationPushJob pushJob = new SeparationPushJob
            {
                Positions = _positionsBack,
                Radii = _radiiBack,
                Grid = _grid,
                CellSize = cellSize,
                PlaneMask = planeMask,
                CellRange = (int3)planeMask,
                PushStrength = _settings.PushStrength,
                MaxPushSpeed = _settings.MaxPushSpeed,
                Push = _pushBack
            };

            _handle = pushJob.Schedule(_scheduledCount, PUSH_BATCH, buildJob.Schedule(_scheduledCount, BUILD_BATCH));
            _scheduled = true;

            (_positionsFront, _positionsBack) = (_positionsBack, _positionsFront);
            (_radiiFront, _radiiBack) = (_radiiBack, _radiiFront);
        }

        /*
         * Growth is doubling rather than exact, because a spawn wave arrives one agent at a time and reallocating
         * seven native containers per agent would cost more than the separation it is there to serve.
         * Only ever called from the tick, where nothing is reading these.
         */
        private void EnsureCapacity(int count)
        {
            if (count <= _capacity) return;

            int capacity = math.max(_capacity * 2, math.max(count, MINIMUM_CAPACITY));

            NativeArray<float3> positionsFront = new NativeArray<float3>(capacity, Allocator.Persistent);
            NativeArray<float3> positionsBack = new NativeArray<float3>(capacity, Allocator.Persistent);
            NativeArray<float> radiiFront = new NativeArray<float>(capacity, Allocator.Persistent);
            NativeArray<float> radiiBack = new NativeArray<float>(capacity, Allocator.Persistent);
            NativeArray<float3> pushFront = new NativeArray<float3>(capacity, Allocator.Persistent);
            NativeArray<float3> pushBack = new NativeArray<float3>(capacity, Allocator.Persistent);

            if (_capacity > 0)
            {
                NativeArray<float3>.Copy(_pushFront, pushFront, _capacity);
                NativeArray<float3>.Copy(_pushBack, pushBack, _capacity);
            }

            DisposeBuffers();

            _positionsFront = positionsFront;
            _positionsBack = positionsBack;
            _radiiFront = radiiFront;
            _radiiBack = radiiBack;
            _pushFront = pushFront;
            _pushBack = pushBack;
            _grid = new NativeParallelMultiHashMap<int, int>(capacity, Allocator.Persistent);
            _capacity = capacity;
        }

        private void DisposeBuffers()
        {
            if (_positionsFront.IsCreated) _positionsFront.Dispose();
            if (_positionsBack.IsCreated) _positionsBack.Dispose();
            if (_radiiFront.IsCreated) _radiiFront.Dispose();
            if (_radiiBack.IsCreated) _radiiBack.Dispose();
            if (_pushFront.IsCreated) _pushFront.Dispose();
            if (_pushBack.IsCreated) _pushBack.Dispose();
            if (_grid.IsCreated) _grid.Dispose();
        }

    }
}
