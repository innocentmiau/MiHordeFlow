using Unity.Mathematics;

namespace MiHordeFlow.Separation
{
    /*
     * The whole contract between the system and whatever is being separated is position in, push out.
     * Nothing here mentions NavMeshAgent, Rigidbody, Transform or a number of dimensions,
     * which is what lets a 3D navmesh scene and a NavMeshPlus 2D scene share one system without either knowing about the other.
     *
     * SeparationIndex is stored on the body rather than looked up in a dictionary because the system does a swap remove
     * on unregister and would otherwise need a hash lookup per agent per frame just to find where its push force landed.
     *
     * GroupSettings doubles as the group identity. Bodies handing back the same settings asset end up in the same group and push against each other;
     * bodies handing back different assets never meet.
     * Using the asset itself rather than a name or an id means there is no registry to keep in sync and no way to typo a body into a group that does not exist.
     */
    /// <summary>
    /// Something the SeparationSystem can push apart from its neighbours.
    /// </summary>
    public interface ISeparationBody
    {

        /// <summary>
        /// Slot this body occupies in its group's arrays, or -1 while it is not registered. Only the system writes this.
        /// </summary>
        int SeparationIndex { get; set; }

        /// <summary>
        /// Which group this body separates within, or null to fall back to the system's default settings.
        /// Read once when the body is registered, so it must not change while the body is registered.
        /// </summary>
        SeparationSettings GroupSettings { get; }

        /// <summary>
        /// How much room this body wants around itself. Two bodies overlap once they are closer than the sum of their radii.
        /// </summary>
        float SeparationRadius { get; }

        /// <summary>
        /// Where the body is right now, sampled once per tick on the main thread.
        /// </summary>
        float3 SeparationPosition { get; }

        /// <summary>
        /// Hands the body its push for this frame.
        /// </summary>
        /// <param name="push">Desired push velocity in world units per second, already clamped by the system.</param>
        /// <param name="deltaTime">Frame time to scale the push by, so the result does not change with framerate.</param>
        void ApplySeparationPush(float3 push, float deltaTime);

    }
}
