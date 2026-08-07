using UnityEngine;

[RequireComponent(typeof(LineRenderer))]
public class BezierConnector : MonoBehaviour
{
    [Header("Connection Points")]
    public Transform pointA;      // player
    public Transform pointB;      // target

    [Header("Curve Settings")]
    [Range(2, 100)]
    public int pointCount = 20;
    public float curveHeight = 2f;
    public float rotationInfluence = 1f;
    public float speedInfluence = 0.5f;
    public float maxSpeedOffset = 5f;

    [Header("Smoothing")]
    public float velocitySmoothing = 8f;   // higher = snappier response

    private LineRenderer line;
    private Vector3 lastPosA;
    private Vector3 rawVelocity;
    private Vector3 smoothVelocity;

    void Awake()
    {
        line = GetComponent<LineRenderer>();
    }

    void Start()
    {
        if (pointA != null) lastPosA = pointA.position;
    }

    void Update()
    {
        if (pointA == null || pointB == null) return;

        // Per-frame displacement converted to velocity
        rawVelocity = (pointA.position - lastPosA) / Time.deltaTime;
        lastPosA = pointA.position;

        // Exponential smoothing removes per-frame jitter and
        // decays the bend back toward rest when the player stops
        smoothVelocity = Vector3.Lerp(
            smoothVelocity, rawVelocity, velocitySmoothing * Time.deltaTime);

        line.positionCount = pointCount;

        Vector3 mid = (pointA.position + pointB.position) / 2f;

        // Clamp so fast movement can't fling the control point away
        Vector3 speedOffset = Vector3.ClampMagnitude(
            smoothVelocity * speedInfluence, maxSpeedOffset);

        Vector3 control = mid
            + Vector3.up * curveHeight
            + pointA.forward * rotationInfluence
            + speedOffset;

        for (int i = 0; i < pointCount; i++)
        {
            float t = i / (float)(pointCount - 1);
            Vector3 pos = GetBezierPoint(pointA.position, control, pointB.position, t);
            line.SetPosition(i, pos);
        }
    }

    // Quadratic Bezier interpolation
    Vector3 GetBezierPoint(Vector3 p0, Vector3 p1, Vector3 p2, float t)
    {
        float u = 1f - t;
        return u * u * p0 + 2f * u * t * p1 + t * t * p2;
    }
}