using UnityEngine;

namespace MiHordeFlow.Separation
{
    /*
     * Split out of the system so the numbers can be tuned in one asset and swapped between scenes,
     * and so a benchmark scene can hold a deliberately harsh profile without touching the one the game ships with.
     * The system builds a default instance in memory when no asset is assigned, so nothing is required to press play.
     */
    /// <summary>
    /// Tuning values for the SeparationSystem, assigned as an asset or left empty for the defaults.
    /// </summary>
    [CreateAssetMenu(fileName = "SeparationSettings", menuName = "MiHordeFlow/Separation Settings", order = 0)]
    public class SeparationSettings : ScriptableObject
    {

        [Header("Scheduling")]
        [SerializeField, Min(1)] private int updateInterval = 1;
        [SerializeField] private SeparationCompletionMode completionMode = SeparationCompletionMode.NEXT_FRAME;
        [SerializeField] private bool reusePushBetweenTicks = true;

        [Header("Grid")]
        [SerializeField] private SeparationPlane plane = SeparationPlane.XZ;
        [SerializeField] private float cellSize = 0f;
        [SerializeField, Min(16)] private int initialCapacity = 1024;

        [Header("Push")]
        [SerializeField] private float pushStrength = 8f;
        [SerializeField] private float maxPushSpeed = 4f;

        /// <summary>
        /// How many frames pass between recalculations. 1 is every frame, 3 is every third frame.
        /// </summary>
        public int UpdateInterval => updateInterval;

        /// <summary>
        /// When the scheduled job is joined back to the main thread.
        /// </summary>
        public SeparationCompletionMode CompletionMode => completionMode;

        /// <summary>
        /// Whether the last computed push keeps being applied on the frames between recalculations, rather than dropping to zero.
        /// </summary>
        public bool ReusePushBetweenTicks => reusePushBetweenTicks;

        /// <summary>
        /// Which axes separation acts on.
        /// </summary>
        public SeparationPlane Plane => plane;

        /// <summary>
        /// Width of one spatial hash cell. Zero or less means it is derived from the largest registered radius each tick.
        /// </summary>
        public float CellSize => cellSize;

        /// <summary>
        /// How many bodies the native arrays are sized for up front, to avoid growing them during a spawn burst.
        /// </summary>
        public int InitialCapacity => initialCapacity;

        /// <summary>
        /// Multiplier on the raw overlap depth, in world units per second of push per unit of overlap.
        /// </summary>
        public float PushStrength => pushStrength;

        /// <summary>
        /// Ceiling on the push velocity any one body can receive, so a body buried in a crowd is not launched.
        /// </summary>
        public float MaxPushSpeed => maxPushSpeed;

    }
}
