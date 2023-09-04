using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

public class DynamicBoneFastMgr : MonoBehaviour
{
    private static DynamicBoneFastMgr m_instance;

    public static DynamicBoneFastMgr Instance
    {
        get
        {
            if (null == m_instance)
            {
                m_instance = GameObject.FindObjectOfType<DynamicBoneFastMgr>();
                if (!m_instance)
                {
                    GameObject go = new GameObject("DynamicBoneFastMgr");
                    m_instance = go.AddComponent<DynamicBoneFastMgr>();
                }
                //m_instance.Init();
            }

            return m_instance;
        }
    }

    #region Job

    [BurstCompile]
    struct RootPosApplyJob : IJobParallelForTransform
    {
        public NativeArray<DynamicBoneFast.HeadInfo> ParticleHeadInfo;

        public void Execute(int index, TransformAccess transform)
        {
            DynamicBoneFast.HeadInfo headInfo = ParticleHeadInfo[index];
            headInfo.m_RootParentBoneWorldPos = transform.position;
            headInfo.m_RootParentBoneWorldRot = transform.rotation;

            ParticleHeadInfo[index] = headInfo;
        }
    }

    [BurstCompile]
    struct PrepareParticleJob : IJob
    {
        [ReadOnly]
        public NativeArray<DynamicBoneFast.HeadInfo> ParticleHeadInfo;
        public NativeArray<DynamicBoneFast.Particle> ParticleInfo;
        public int HeadCount;

        public void Execute()
        {
            for (int i = 0; i < HeadCount; i++)
            {
                DynamicBoneFast.HeadInfo curHeadInfo = ParticleHeadInfo[i];

                float3 parentPosition = curHeadInfo.m_RootParentBoneWorldPos;
                quaternion parentRotation = curHeadInfo.m_RootParentBoneWorldRot;

                for (int j = 0; j < curHeadInfo.m_particleCount; j++)
                {
                    int pIdx = curHeadInfo.m_jobDataOffset + j;
                    DynamicBoneFast.Particle p = ParticleInfo[pIdx];

                    var localPosition = p.localPosition * p.parentScale;
                    var localRotation = p.localRotation;
                    var worldPosition = parentPosition + math.mul(parentRotation, localPosition);
                    var worldRotation = math.mul(parentRotation, localRotation);

                    p.worldPosition = worldPosition;
                    p.worldRotation = worldRotation;

                    parentPosition = worldPosition;
                    parentRotation = worldRotation;

                    ParticleInfo[pIdx] = p;
                }
            }
        }
    }

    [BurstCompile]
    struct UpdateParticles1Job : IJobParallelFor
    {
        [ReadOnly]
        public NativeArray<DynamicBoneFast.HeadInfo> ParticleHeadInfo;
        public NativeArray<DynamicBoneFast.Particle> ParticleInfo;
        public int HeadCount;

        public void Execute(int index)
        {
            int headIndex = index / DynamicBoneFast.MAX_TRANSFORM_LIMIT;
            DynamicBoneFast.HeadInfo curHeadInfo = ParticleHeadInfo[headIndex];
            int singleId = index % DynamicBoneFast.MAX_TRANSFORM_LIMIT;

            if (singleId >= curHeadInfo.m_particleCount)
                return;

            int pIdx = curHeadInfo.m_jobDataOffset + (index % DynamicBoneFast.MAX_TRANSFORM_LIMIT);

            DynamicBoneFast.Particle p = ParticleInfo[pIdx];

            if (p.m_ParentIndex >= 0)
            {
                float3 ev = p.tmpWorldPosition - p.tmpPrevWorldPosition;
                float3 evrmove = curHeadInfo.m_ObjectMove * p.m_Inert;
                p.tmpPrevWorldPosition = p.tmpWorldPosition + evrmove;

                float edamping = p.m_Damping;
                float3 tmp = ev * (1 - edamping) + evrmove;
                p.tmpWorldPosition += tmp;
            }
            else
            {
                p.tmpPrevWorldPosition = p.tmpWorldPosition;
                p.tmpWorldPosition = p.worldPosition;
            }

            ParticleInfo[pIdx] = p;
        }
    }

    [BurstCompile]
    struct UpdateParticle2Job : IJobParallelFor
    {
        [ReadOnly]
        public NativeArray<DynamicBoneFast.HeadInfo> ParticleHeadInfo;
        public NativeArray<DynamicBoneFast.Particle> ParticleInfo;
        public int HeadCount;
        public float DeltaTime;

        public void Execute(int index)
        {
            if (index % DynamicBoneFast.MAX_TRANSFORM_LIMIT == 0)
                return;

            int headIndex = index / DynamicBoneFast.MAX_TRANSFORM_LIMIT;
            DynamicBoneFast.HeadInfo curHeadInfo = ParticleHeadInfo[headIndex];

            int singleId = index % DynamicBoneFast.MAX_TRANSFORM_LIMIT;

            if (singleId >= curHeadInfo.m_particleCount)
                return;

            int pIdx = curHeadInfo.m_jobDataOffset + (index % DynamicBoneFast.MAX_TRANSFORM_LIMIT);

            DynamicBoneFast.Particle p = ParticleInfo[pIdx];
            int p0Idx = curHeadInfo.m_jobDataOffset + p.m_ParentIndex;
            DynamicBoneFast.Particle p0 = ParticleInfo[p0Idx];

            float3 ePos = p.worldPosition;
            float3 ep0Pos = p0.worldPosition;

            float erestLen = math.distance(ep0Pos, ePos);

            float stiffness = p.m_Stiffness;
            if (stiffness > 0 || p.m_Elasticity > 0)
            {
                float4x4 em0 = float4x4.TRS(p0.tmpWorldPosition, p0.worldRotation, p.parentScale);
                float3 erestPos = math.mul(em0, new float4(p.localPosition.xyz, 1)).xyz;
                float3 ed = erestPos - p.tmpWorldPosition;
                float3 eStepElasticity = ed * p.m_Elasticity * curHeadInfo.m_UpdateRate * DeltaTime;
                p.tmpWorldPosition += eStepElasticity;

                if (stiffness > 0)
                {
                    float len = math.distance(erestPos, p.tmpWorldPosition);
                    float maxlen = erestLen * (1 - stiffness) * 2;
                    if (len > maxlen)
                    {
                        float3 max = ed * ((len - maxlen) / len);
                        p.tmpWorldPosition += max;
                    }
                }
            }

            float3 edd = p0.tmpWorldPosition - p.tmpWorldPosition;
            float eleng = math.distance(p0.tmpWorldPosition, p.tmpWorldPosition);
            if (eleng > 0)
            {
                float3 tmp = edd * ((eleng - erestLen) / eleng);
                p.tmpWorldPosition += tmp;
            }

            ParticleInfo[pIdx] = p;
        }
    }

    [BurstCompile]
    struct ApplyParticleToTransform : IJobParallelFor
    {
        [ReadOnly]
        public NativeArray<DynamicBoneFast.HeadInfo> ParticleHeadInfo;
        public NativeArray<DynamicBoneFast.Particle> ParticleInfo;
        public int HeadCount;

        public void Execute(int index)
        {
            if (index % DynamicBoneFast.MAX_TRANSFORM_LIMIT == 0)
                return;

            int headIndex = index / DynamicBoneFast.MAX_TRANSFORM_LIMIT;

            DynamicBoneFast.HeadInfo curHeadInfo = ParticleHeadInfo[headIndex];
            int singleId = index % DynamicBoneFast.MAX_TRANSFORM_LIMIT;

            if (singleId >= curHeadInfo.m_particleCount)
                return;

            int pIdx = curHeadInfo.m_jobDataOffset + (index % DynamicBoneFast.MAX_TRANSFORM_LIMIT);

            DynamicBoneFast.Particle p = ParticleInfo[pIdx];
            int p0Idx = curHeadInfo.m_jobDataOffset + p.m_ParentIndex;
            DynamicBoneFast.Particle p0 = ParticleInfo[p0Idx];

            if (p0.m_ChildCount <= 1)
            {
                float3 ev = p.localPosition;
                float3 ev2 = p.tmpWorldPosition - p0.tmpWorldPosition;

                float4x4 epm = float4x4.TRS(p.worldPosition, p.worldRotation, p.parentScale);

                var worldV = math.mul(epm, new float4(ev, 0)).xyz;
                Quaternion erot = Quaternion.FromToRotation(worldV, ev2);
                var eoutputRot = math.mul(erot, p.worldRotation);
                p0.worldRotation = eoutputRot;
            }

            p.worldPosition = p.tmpWorldPosition;

            ParticleInfo[pIdx] = p;
            ParticleInfo[p0Idx] = p0;
        }
    }

    [BurstCompile]
    struct FinalJob : IJobParallelForTransform
    {
        [ReadOnly]
        public NativeArray<DynamicBoneFast.Particle> ParticleInfo;

        public void Execute(int index, TransformAccess transform)
        {
            transform.rotation = ParticleInfo[index].worldRotation;
            transform.position = ParticleInfo[index].worldPosition;
        }
    }

    #endregion


    List<DynamicBoneFast> m_DynamicBoneList;
    NativeList<DynamicBoneFast.Particle> m_ParticleInfoList;
    NativeList<DynamicBoneFast.HeadInfo> m_HeadInfoList;

    TransformAccessArray m_headRootTransform;
    TransformAccessArray m_particleTransformArr;
    int m_DbDataLen;
    JobHandle m_lastJobHandle;

    Queue<DynamicBoneFast> m_loadingQueue = new Queue<DynamicBoneFast>();
    Queue<DynamicBoneFast> m_removeQueue = new Queue<DynamicBoneFast>();


    void Awake()
    {
        m_instance = this;
        m_DynamicBoneList = new List<DynamicBoneFast>();
        m_ParticleInfoList = new NativeList<DynamicBoneFast.Particle>(Allocator.Persistent);
        m_HeadInfoList = new NativeList<DynamicBoneFast.HeadInfo>(Allocator.Persistent);
        m_particleTransformArr = new TransformAccessArray(200 * DynamicBoneFast.MAX_TRANSFORM_LIMIT, 64);
        m_headRootTransform = new TransformAccessArray(200, 64);
    }

    void UpdateQueue()
    {
        while (m_loadingQueue.Count > 0)
        {
            DynamicBoneFast target = m_loadingQueue.Dequeue();
            int index = m_DynamicBoneList.IndexOf(target);
            if (index != -1)
                continue;

            m_DynamicBoneList.Add(target);
            target.m_headInfo.m_jobDataOffset = m_ParticleInfoList.Length;
            target.m_headInfo.HeadIndex = m_HeadInfoList.Length;
            m_HeadInfoList.Add(target.m_headInfo);
            m_ParticleInfoList.AddRange(target.m_Particles);
            m_headRootTransform.Add(target.m_rootParentTransform);

            for (int i = 0; i < DynamicBoneFast.MAX_TRANSFORM_LIMIT; i++)
                m_particleTransformArr.Add(target.m_particleTransformArr[i]);

            m_DbDataLen++;
        }

        while (m_removeQueue.Count > 0)
        {
            DynamicBoneFast target = m_removeQueue.Dequeue();
            int index = m_DynamicBoneList.IndexOf(target);
            if (index != -1)
            {
                m_DynamicBoneList.RemoveAt(index);

                int curHeadIndex = target.m_headInfo.HeadIndex;

                //是否是队列中末尾对象
                bool isEndTarget = curHeadIndex == m_HeadInfoList.Length - 1;
                if (isEndTarget)
                {
                    m_HeadInfoList.RemoveAtSwapBack(curHeadIndex);
                    m_headRootTransform.RemoveAtSwapBack(curHeadIndex);

                    for (int i = DynamicBoneFast.MAX_TRANSFORM_LIMIT - 1; i >= 0; i--)
                    {
                        int dataOffset = curHeadIndex * DynamicBoneFast.MAX_TRANSFORM_LIMIT + i;
                        m_ParticleInfoList.RemoveAtSwapBack(dataOffset);
                        m_particleTransformArr.RemoveAtSwapBack(dataOffset);
                    }
                }
                else
                {
                    //将最末列的HeadInfo 索引设置为当前将要移除的HeadInfo 索引
                    DynamicBoneFast lastTarget = m_DynamicBoneList[m_DynamicBoneList.Count - 1];
                    m_DynamicBoneList.RemoveAt(m_DynamicBoneList.Count - 1);
                    m_DynamicBoneList.Insert(index, lastTarget);

                    DynamicBoneFast.HeadInfo lastHeadInfo = lastTarget.ResetHeadIndexAndDataOffset(curHeadIndex);
                    m_HeadInfoList.RemoveAtSwapBack(curHeadIndex);
                    m_HeadInfoList[curHeadIndex] = lastHeadInfo;
                    m_headRootTransform.RemoveAtSwapBack(curHeadIndex);

                    for (int i = DynamicBoneFast.MAX_TRANSFORM_LIMIT - 1; i >= 0; i--)
                    {
                        int dataOffset = curHeadIndex * DynamicBoneFast.MAX_TRANSFORM_LIMIT + i;
                        m_ParticleInfoList.RemoveAtSwapBack(dataOffset);
                        m_particleTransformArr.RemoveAtSwapBack(dataOffset);
                    }
                }

                m_DbDataLen--;
            }

            target.ClearJobData();
        }
    }

    public void OnEnter(DynamicBoneFast target)
    {
        m_loadingQueue.Enqueue(target);
    }

    public void OnExit(DynamicBoneFast target)
    {
        m_removeQueue.Enqueue(target);
    }

    void LateUpdate()
    {
        if (!m_lastJobHandle.IsCompleted)
            return;

        m_lastJobHandle.Complete();

        UpdateQueue();

        if (m_DbDataLen == 0)
            return;

        var dataArrLength = m_DbDataLen * DynamicBoneFast.MAX_TRANSFORM_LIMIT;

        var rootJob = new RootPosApplyJob
        {
            ParticleHeadInfo = m_HeadInfoList
        };
        var rootHandle = rootJob.Schedule(m_headRootTransform);

        var prepareJob = new PrepareParticleJob
        {
            ParticleHeadInfo = m_HeadInfoList,
            ParticleInfo = m_ParticleInfoList,
            HeadCount = m_DbDataLen
        };
        var prepareHandle = prepareJob.Schedule(rootHandle);

        var update1Job = new UpdateParticles1Job
        {
            ParticleHeadInfo = m_HeadInfoList,
            ParticleInfo = m_ParticleInfoList,
            HeadCount = m_DbDataLen
        };
        var update1Handle = update1Job.Schedule(dataArrLength, DynamicBoneFast.MAX_TRANSFORM_LIMIT, prepareHandle);

        var update2Job = new UpdateParticle2Job
        {
            ParticleHeadInfo = m_HeadInfoList,
            ParticleInfo = m_ParticleInfoList,
            HeadCount = m_DbDataLen,
            DeltaTime = Time.deltaTime,
        };
        var update2Handle = update2Job.Schedule(dataArrLength, DynamicBoneFast.MAX_TRANSFORM_LIMIT, update1Handle);

        var appTransJob = new ApplyParticleToTransform
        {
            ParticleHeadInfo = m_HeadInfoList,
            ParticleInfo = m_ParticleInfoList,
            HeadCount = m_DbDataLen
        };

        var appTransHandle = appTransJob.Schedule(dataArrLength, DynamicBoneFast.MAX_TRANSFORM_LIMIT, update2Handle);
        var finalJob = new FinalJob
        {
            ParticleInfo = m_ParticleInfoList,
        };
        var finalHandle = finalJob.Schedule(m_particleTransformArr, appTransHandle);

        m_lastJobHandle = finalHandle;

        JobHandle.ScheduleBatchedJobs();
    }

    void OnDisable()
    {
        m_lastJobHandle.Complete();

        if (m_particleTransformArr.isCreated)
        {
            m_particleTransformArr.Dispose();
        }

        if (m_ParticleInfoList.IsCreated)
        {
            m_ParticleInfoList.Dispose();
        }

        if (m_HeadInfoList.IsCreated)
        {
            m_HeadInfoList.Dispose();
        }

        if (m_headRootTransform.isCreated)
        {
            m_headRootTransform.Dispose();
        }
    }
}