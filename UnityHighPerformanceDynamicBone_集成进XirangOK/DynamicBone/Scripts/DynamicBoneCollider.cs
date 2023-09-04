using System;
using Unity.Burst;
using UnityEngine;
using Unity.Mathematics;
using UnityEngine.Jobs;
using UnityEngine.Serialization;

public enum DynamicBoneDirection
{
    X,
    Y,
    Z
}

public enum DynamicBoneBound
{
    Outside,
    Inside
}

public struct DynamicBoneColliderInfo
{
    public int Index;
    public bool IsGlobal;
    public DynamicBoneBound Bound;
    public float Height;
    public float Radius;
    public float3 Center;
    public DynamicBoneDirection Direction;
    public float Scale;
    public float3 Position;
    public quaternion Rotation;
}

public class DynamicBoneCollider : MonoBehaviour
{
    [Tooltip("是否为全局碰撞器")][SerializeField] private bool isGlobal = false;

    [Tooltip("碰撞器半径")][SerializeField] private float radius = 0.5f;

    [Tooltip("高度，大于0即为胶囊体")]
    [SerializeField]
    private float height = 0;

    [Tooltip("高度的轴向")][SerializeField] private DynamicBoneDirection direction = DynamicBoneDirection.Y;

    [Tooltip("碰撞器中心位置， 相对于挂载物体的局部空间")]
    [SerializeField]
    private Vector3 center = Vector3.zero;

    [Tooltip("把骨骼束缚在外面或里面")]
    [SerializeField]
    private DynamicBoneBound bound = DynamicBoneBound.Outside;


    [HideInInspector] public DynamicBoneColliderInfo ColliderInfo;

    private bool hasInitialized;

    private void OnValidate()
    {
        if (!hasInitialized) return;
        if (Application.isEditor && Application.isPlaying)
        {
            ColliderInfo.Radius = math.max(radius, 0);
            ColliderInfo.Height = math.max(height, 0);
            ColliderInfo.Bound = bound;
            ColliderInfo.Center = center;
            ColliderInfo.Direction = direction;
            DynamicBoneManager.Instance.RefreshColliderInfo(ColliderInfo);
        }
    }

    private void Awake()
    {
        ColliderInfo = new DynamicBoneColliderInfo
        {
            IsGlobal = isGlobal,
            Center = center,
            Radius = radius,
            Height = height,
            Direction = direction,
            Bound = bound,
            Scale = transform.lossyScale.x,
        };
        DynamicBoneManager.Instance.AddCollider(this);
        hasInitialized = true;
    }

    //private void OnDrawGizmosSelected()
    //{
    //    if (!enabled)
    //        return;

    //    if (bound == DynamicBoneBound.Outside)
    //        Gizmos.color = Color.yellow;
    //    else
    //        Gizmos.color = Color.magenta;
    //    float radius = this.radius * math.abs(transform.lossyScale.x);
    //    float h = height * 0.5f - this.radius;
    //    if (h <= 0)
    //    {
    //        Gizmos.DrawWireSphere(transform.TransformPoint(center), radius);
    //    }
    //    else
    //    {
    //        float3 c0 = center;
    //        float3 c1 = center;

    //        switch (direction)
    //        {
    //            case DynamicBoneDirection.X:
    //                c0.x -= h;
    //                c1.x += h;
    //                break;
    //            case DynamicBoneDirection.Y:
    //                c0.y -= h;
    //                c1.y += h;
    //                break;
    //            case DynamicBoneDirection.Z:
    //                c0.z -= h;
    //                c1.z += h;
    //                break;
    //        }

    //        Gizmos.DrawWireSphere(transform.TransformPoint(c0), radius);
    //        Gizmos.DrawWireSphere(transform.TransformPoint(c1), radius);
    //    }
    //}

    public static bool HandleCollision(in DynamicBoneColliderInfo collider, ref float3 particlePosition,
        in float particleRadius)
    {
        float radius = collider.Radius * math.abs(collider.Scale);
        float h = collider.Height * 0.5f - radius;

        float3 worldPosition = DynamicBoneUtil.LocalToWorldPosition(collider.Position, collider.Rotation, collider.Center);

        if (h <= 0)
        {
            return collider.Bound == DynamicBoneBound.Outside
                ? OutsideSphere(ref particlePosition, particleRadius, worldPosition, radius)
                : InsideSphere(ref particlePosition, particleRadius, worldPosition, radius);
        }

        float3 center1 = collider.Center;
        float3 center2 = collider.Center;

        switch (collider.Direction)
        {
            case DynamicBoneDirection.X:
                center1.x -= h;
                center2.x += h;
                break;
            case DynamicBoneDirection.Y:
                center1.y -= h;
                center2.y += h;
                break;
            case DynamicBoneDirection.Z:
                center1.z -= h;
                center2.z += h;
                break;
        }


        float3 worldCenter1 = DynamicBoneUtil.LocalToWorldPosition(collider.Position, collider.Rotation, center1);
        float3 worldCenter2 = DynamicBoneUtil.LocalToWorldPosition(collider.Position, collider.Rotation, center2);

        return collider.Bound == DynamicBoneBound.Outside
            ? OutsideCapsule(ref particlePosition, particleRadius, worldCenter1,
                worldCenter2, radius)
            : InsideCapsule(ref particlePosition, particleRadius, worldCenter1,
                worldCenter2, radius);
    }


    private static bool OutsideSphere(ref float3 particlePosition, float particleRadius, float3 sphereCenter,
        float sphereRadius)
    {
        float r = sphereRadius + particleRadius;
        float r2 = r * r;
        float3 d = particlePosition - sphereCenter;

        float len2 = math.lengthsq(d);

        // if is inside sphere, project onto sphere surface
        if (len2 > 0 && len2 < r2)
        {
            float len = math.sqrt(len2);
            particlePosition = sphereCenter + d * (r / len);
            return true;
        }

        return false;
    }

    private static bool InsideSphere(ref float3 particlePosition, float particleRadius, float3 sphereCenter,
        float sphereRadius)
    {
        float r = sphereRadius - particleRadius;
        float r2 = r * r;
        float3 d = particlePosition - sphereCenter;
        float len2 = math.lengthsq(d);

        // if is outside sphere, project onto sphere surface
        if (len2 > r2)
        {
            float len = math.sqrt(len2);
            particlePosition = sphereCenter + d * (r / len);
            return true;
        }

        return false;
    }


    private static bool OutsideCapsule(ref float3 particlePosition, float particleRadius, float3 capsuleP0,
        float3 capsuleP1,
        float capsuleRadius)
    {
        float r = capsuleRadius + particleRadius;
        float r2 = r * r;
        float3 dir = capsuleP1 - capsuleP0;
        float3 d = particlePosition - capsuleP0;
        float t = math.dot(d, dir);

        if (t <= 0)
        {
            // check sphere1
            float len2 = math.lengthsq(d);
            if (len2 > 0 && len2 < r2)
            {
                float len = math.sqrt(len2);
                particlePosition = capsuleP0 + d * (r / len);
                return true;
            }
        }
        else
        {
            float dl = math.lengthsq(dir);
            if (t >= dl)
            {
                // check sphere2
                d = particlePosition - capsuleP1;
                float len2 = math.lengthsq(d);
                if (len2 > 0 && len2 < r2)
                {
                    float len = math.sqrt(len2);
                    particlePosition = capsuleP1 + d * (r / len);
                    return true;
                }
            }
            else if (dl > 0)
            {
                // check cylinder
                t /= dl;
                d -= dir * t;
                float len2 = math.lengthsq(d);


                if (len2 > 0 && len2 < r2)
                {
                    float len = math.sqrt(len2);
                    particlePosition += d * ((r - len) / len);
                    return true;
                }
            }
        }

        return false;
    }


    private static bool InsideCapsule(ref float3 particlePosition, float particleRadius, float3 capsuleP0,
        float3 capsuleP1,
        float capsuleRadius)
    {
        float r = capsuleRadius - particleRadius;
        float r2 = r * r;
        float3 dir = capsuleP1 - capsuleP0;
        float3 d = particlePosition - capsuleP0;
        float t = math.dot(d, dir);

        if (t <= 0)
        {
            // check sphere1
            float len2 = math.lengthsq(d);
            if (len2 > r2)
            {
                float len = Mathf.Sqrt(len2);
                particlePosition = capsuleP0 + d * (r / len);
                return true;
            }
        }
        else
        {
            float dl = math.lengthsq(dir);
            if (t >= dl)
            {
                // check sphere2
                d = particlePosition - capsuleP1;
                float len2 = math.lengthsq(d);
                if (len2 > r2)
                {
                    float len = Mathf.Sqrt(len2);
                    particlePosition = capsuleP1 + d * (r / len);
                    return true;
                }
            }
            else if (dl > 0)
            {
                // check cylinder
                t /= dl;
                d -= dir * t;
                float len2 = math.lengthsq(d);
                if (len2 > r2)
                {
                    float len = Mathf.Sqrt(len2);
                    particlePosition += d * ((r - len) / len);
                    return true;
                }
            }
        }

        return false;
    }
}
