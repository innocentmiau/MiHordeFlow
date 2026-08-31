using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MiHordeFlow.Separation
{
    /*
     * This is reactive, not predictive. It does not look at where anybody is heading, it looks at who is already standing inside somebody else and pushes them out.
     * Agents are allowed to overlap for a frame or two, which is the whole trade:
     * full RVO costs a solve per agent per neighbour and is what the built in avoidance was spending the main thread on,
     * and this costs one subtract and one square root per overlapping pair.
     *
     * The cell range is derived from the plane mask instead of being a fixed 3x3x3.
     * On the XZ plane every agent shares cell zero on Y, so scanning the Y neighbours would triple the lookups to find nothing.
     *
     * Two agents at the exact same position have no direction to separate along, which happens more than it sounds like when a pool spawns a batch at one point.
     * Those get a direction seeded from the pair of indices, so it is stable frame to frame and opposite for the two of them rather than jittering them into each other.
     */
    /// <summary>
    /// Computes the push velocity that pulls each agent out of whichever neighbours it is currently overlapping.
    /// </summary>
    [BurstCompile(FloatPrecision.Low, FloatMode.Fast)]
    public struct SeparationPushJob : IJobParallelFor
    {

        private const float COINCIDENT_EPSILON_SQUARED = .0001f;
        private const float SCATTER_TURN = 6.2831853f;
        private const float SCATTER_SKEW = 1.7f;

        [ReadOnly] public NativeArray<float3> Positions;
        [ReadOnly] public NativeArray<float> Radii;
        [ReadOnly] public NativeParallelMultiHashMap<int, int> Grid;

        public float CellSize;
        public float3 PlaneMask;
        public int3 CellRange;
        public float PushStrength;
        public float MaxPushSpeed;

        [WriteOnly] public NativeArray<float3> Push;

        public void Execute(int index)
        {
            float3 position = Positions[index] * PlaneMask;
            float radius = Radii[index];
            float3 accumulated = float3.zero;
            int3 baseCell = SpatialHash.CellOf(position, CellSize);

            for (int x = -CellRange.x; x <= CellRange.x; x++)
            for (int y = -CellRange.y; y <= CellRange.y; y++)
            for (int z = -CellRange.z; z <= CellRange.z; z++)
                accumulated += PushFromCell(index, position, radius, baseCell + new int3(x, y, z));

            accumulated *= PushStrength;

            float speedSquared = math.lengthsq(accumulated);
            Push[index] = speedSquared > MaxPushSpeed * MaxPushSpeed ? accumulated * (MaxPushSpeed / math.sqrt(speedSquared)) : accumulated;
        }

        /// <summary>
        /// Total overlap depth pushed back at one agent by everything bucketed in a single cell.
        /// </summary>
        private float3 PushFromCell(int index, float3 position, float radius, int3 cell)
        {
            if (!Grid.TryGetFirstValue(SpatialHash.KeyOf(cell), out int other, out NativeParallelMultiHashMapIterator<int> iterator)) return float3.zero;

            float3 accumulated = float3.zero;

            do
            {
                if (other == index) continue;

                float3 delta = position - Positions[other] * PlaneMask;
                float minimumDistance = radius + Radii[other];
                float distanceSquared = math.lengthsq(delta);

                if (distanceSquared >= minimumDistance * minimumDistance) continue;

                if (distanceSquared < COINCIDENT_EPSILON_SQUARED)
                {
                    accumulated += ScatterDirection(index, other) * minimumDistance;
                    continue;
                }

                float distance = math.sqrt(distanceSquared);
                accumulated += delta * ((minimumDistance - distance) / distance);
            }
            while (Grid.TryGetNextValue(out other, ref iterator));

            return accumulated;
        }

        /// <summary>
        /// A repeatable direction for a pair of agents sitting on top of each other, opposite for each of the two.
        /// </summary>
        private float3 ScatterDirection(int index, int other)
        {
            uint seed = math.hash(new int2(math.min(index, other), math.max(index, other)));
            float angle = seed * (SCATTER_TURN / uint.MaxValue);
            float3 spread = new float3(math.cos(angle), math.sin(angle * SCATTER_SKEW), math.sin(angle));
            float3 direction = math.normalizesafe(spread * PlaneMask, new float3(1f, 0f, 0f) * PlaneMask);
            return index < other ? direction : -direction;
        }

    }
}
