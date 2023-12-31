using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using UCL.CASMS.DH;
using UnityEngine;

public enum Fingers : int
{
    Thumb = 0,
    Index = 1,
    Middle = 2,
    Ring = 3,
    Little = 4
}

/// <summary>
/// Estimes the pose of the Hand1 Kinematic Hand Model built into the Solver.
/// </summary>
public class Hand1Solver : PoseSolver
{
    // The Hand1 parameterisation is based on DH chains,
    // one per finger.

    [StructLayout(LayoutKind.Sequential)]
    public struct JointParams
    {
        public double d;
        public double theta;
        public double r;
        public double a;
        public double min;
        public double max;

        public static implicit operator JointParams(UCL.CASMS.DH.Joint j)
        {
            return new JointParams()
            {
                a = j.a * Mathf.Deg2Rad,
                d = j.d,
                r = j.r,
                theta = j.th * Mathf.Deg2Rad,
                min = j.min_th * Mathf.Deg2Rad,
                max = j.max_th * Mathf.Deg2Rad
            };
        }
    }

    // The following types are effectively manually unrolled versions of
    // the native array based ones.

    [StructLayout(LayoutKind.Sequential)]
    public struct ChainParams
    {
        public JointParams joint1;
        public JointParams joint2;
        public JointParams joint3;
        public JointParams joint4;
        public JointParams joint5;
        public JointParams joint6;

        public JointParams Joint(int i)
        {
            switch (i)
            {
                case 0:
                    return joint1;
                case 1:
                    return joint2;
                case 2:
                    return joint3;
                case 3:
                    return joint4;
                case 4:
                    return joint5;
                case 5:
                    return joint6;
            }
            throw new ArgumentException();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HandParams
    {
        public ChainParams thumb;
        public ChainParams index;
        public ChainParams middle;
        public ChainParams ring;
        public ChainParams little;

        public ChainParams Chain(int i)
        {
            switch (i)
            {
                case 0:
                    return thumb;
                case 1:
                    return index;
                case 2:
                    return middle;
                case 3:
                    return ring;
                case 4:
                    return little;
            }
            throw new ArgumentException();
        }
    }

    private HandParams hand;

    private ChainParams GetChainParams(Transform root)
    {
        var nodes = root.GetComponentsInChildren<DHJointLink>();
        ChainParams p;
        p.joint1 = nodes[0].joint;
        p.joint2 = nodes[1].joint;
        p.joint3 = nodes[2].joint;
        p.joint4 = nodes[3].joint;
        p.joint5 = nodes[4].joint;
        p.joint6 = nodes[5].joint;
        return p;
    }

    private List<DHJointLink[]> FingerChains;

    private double[] angles;

    private DHJointLink[] GetChainNodes(Transform root)
    {
        return root.GetComponentsInChildren<DHJointLink>();
    }

    private IntPtr startPose;
    private IntPtr hand1;

    public void Initialise()
    {
        // Get the joints (per-chain) in a representation amenable for loading
        // into the solver.

        // This implementation assumes that each finger has a separate GameObject,
        // directly below the Hand to which the Solver is added.

        hand.thumb = GetChainParams(transform.Find("Thumb"));
        hand.index = GetChainParams(transform.Find("Index"));
        hand.middle = GetChainParams(transform.Find("Middle"));
        hand.ring = GetChainParams(transform.Find("Ring"));
        hand.little = GetChainParams(transform.Find("Little"));

        FingerChains = new List<DHJointLink[]>();

        FingerChains.Add(GetChainNodes(transform.Find("Thumb")));
        FingerChains.Add(GetChainNodes(transform.Find("Index")));
        FingerChains.Add(GetChainNodes(transform.Find("Middle")));
        FingerChains.Add(GetChainNodes(transform.Find("Ring")));
        FingerChains.Add(GetChainNodes(transform.Find("Little")));

        angles = new double[30];

        // Create the entries in the problem

        initialise();

        startPose = addPose(true);   // The pose parameter for the root of the hand/wrist
        hand1 = addHand1(hand, startPose);
    }

    public TransformPointMeasurement AddMeasurement(Fingers finger, Vector3 point)
    {
        var endPoint = FingerChains[(int)finger].Last().Endpoint;
        TransformPointMeasurement m = new TransformPointMeasurement();
        m.point = point;
        m.offset = endPoint.transform.InverseTransformPoint(point);
        m.PoseR = getHand1EndPose(hand1, finger);
        m.Ref = addPointMeasurement(m.PoseR, m.offset.x, m.offset.y, m.offset.z, m.point.x, m.point.y, m.point.z);
        return m;
    }

    private void Start()
    {
        Initialise();
    }

    public void Update()
    {
        solve();

        // Project the current angles onto the DH Nodes in the Hand Object

        getHand1Pose(hand1, angles);

        for (int f = 0; f < 5; f++)
        {
            var offset = f * 6;
            var joints = FingerChains[f];
            for (int i = 0; i < 6; i++)
            {
                joints[i].joint.th = (float)angles[offset + i] * Mathf.Rad2Deg;
            }
        }
    }

    private void OnDrawGizmos()
    {
        if (!isActiveAndEnabled)
        {
            return;
        }

        Gizmos.color = Color.green;

        if (hand1 != IntPtr.Zero)
        {
            var origin = getPose(startPose);
            getHand1Pose(hand1, angles);

            var anglesList = "";
            foreach (var item in angles)
            {
                anglesList += item.ToString() + "\n";
            }

#if UNITY_EDITOR
            UnityEditor.Handles.color = Color.yellow;
            UnityEditor.Handles.Label(transform.position, $"{anglesList}");
#endif

            for (int i = 0; i < 5; i++)
            {
                var p = getPose(getHand1EndPose(hand1, (Fingers)i));
                Gizmos.DrawWireSphere(p.Position, 0.005f);
            }
            
            Gizmos.color = Color.green;

            for (int i = 0; i < 5; i++)
            {
                OnDrawChainGizmos(i, origin);
            }
        }
    }

    /// <summary>
    /// Draws a sequence of JointParams as lines, using the angles in angles to
    /// set the rotation of each joint.
    /// </summary>
    /// <remarks>
    /// To do this, the method keeps track of the last endpoint, and its rotation,
    /// as it progresses through the chain.
    /// </remarks>
    private void OnDrawChainGizmos(int chain, Pose origin)
    {
        var p = hand.Chain(chain);

        var rotation = origin.Rotation;
        var position = origin.Position;

        for (int i = 0; i < 6; i++)
        {
            var joint = p.Joint(i);
            var theta = angles[(chain * 6) + i];

            var offset = rotation * Vector3.up * (float)joint.d +
                rotation * (Quaternion.AngleAxis((float)theta * Mathf.Rad2Deg, Vector3.up) * Vector3.forward * (float)joint.r);

            Gizmos.DrawLine(position, position + offset);

            // Note that we do not include the final rotation around the normal
            // above since this doesn't affect the endpoint of the chain on which
            // it exists, but we do add it below since it affects subsequent
            // joints.

            rotation = rotation * (Quaternion.AngleAxis((float)theta * Mathf.Rad2Deg, Vector3.up) * Quaternion.AngleAxis((float)joint.a * Mathf.Rad2Deg, Vector3.forward));
            position = position + offset;
        }
    }
}
