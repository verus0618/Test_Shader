using UnityEngine;

public class BezierConnector : MonoBehaviour
{
    [Header("Connection Points")]
    public Transform pointA;      // player
    public Transform pointB;      // target

    [Header("Curve Settings")]
    [Range(2, 100)]
    public int pointCount = 20;
    public float curveHeight = 2f;

    [Header("Movement Bend")]
    public float speedInfluence = 0.5f;    // bend toward travel direction
    public float maxSpeedOffset = 5f;
    public float velocitySmoothing = 8f;

    [Header("Turn Bend")]
    public float turnInfluence = 0.4f;     // bend toward turn direction
    public float maxTurnOffset = 5f;
    public float turnSmoothing = 8f;

    [Header("Multiple Lines")]
    public LineRenderer[] lines;
    public float lineSpread = 0.15f;

    private Vector3 lastPosA;
    private Quaternion lastRotA;
    private Vector3 smoothVelocity;
    private Vector3 smoothTurnOffset;

    void Start()
    {
        if (pointA != null)
        {
            lastPosA = pointA.position;
            lastRotA = pointA.rotation;
        }
    }

    void Update()
    {
        if (pointA == null || pointB == null || lines == null || lines.Length == 0) return;

        // Linear velocity -> bend toward movement direction
        Vector3 rawVelocity = (pointA.position - lastPosA) / Time.deltaTime;
        lastPosA = pointA.position;
        smoothVelocity = Vector3.Lerp(
            smoothVelocity, rawVelocity, velocitySmoothing * Time.deltaTime);

        Vector3 speedOffset = Vector3.ClampMagnitude(
            smoothVelocity * speedInfluence, maxSpeedOffset);

        // Signed yaw speed -> bend toward turn direction (player's right axis)
        Quaternion deltaRot = pointA.rotation * Quaternion.Inverse(lastRotA);
        lastRotA = pointA.rotation;
        deltaRot.ToAngleAxis(out float angle, out Vector3 axis);
        if (angle > 180f) angle -= 360f;
        float yawSpeed = axis.y * angle / Time.deltaTime;   // signed deg/s

        Vector3 rawTurnOffset = pointA.right * (yawSpeed * turnInfluence * 0.01f);
        rawTurnOffset = Vector3.ClampMagnitude(rawTurnOffset, maxTurnOffset);
        smoothTurnOffset = Vector3.Lerp(
            smoothTurnOffset, rawTurnOffset, turnSmoothing * Time.deltaTime);

        Vector3 mid = (pointA.position + pointB.position) / 2f;

        // Control point: base lift + both direction-driven bends
        Vector3 control = mid
            + Vector3.up * curveHeight
            + speedOffset
            + smoothTurnOffset;

        Vector3 dir = (pointB.position - pointA.position).normalized;
        Vector3 sideAxis = Vector3.Cross(dir, Vector3.up).normalized;

        for (int l = 0; l < lines.Length; l++)
        {
            if (lines[l] == null) continue;
            lines[l].positionCount = pointCount;

            float centered = l - (lines.Length - 1) / 2f;
            Vector3 lateral = sideAxis * centered * lineSpread;

            for (int i = 0; i < pointCount; i++)
            {
                float t = i / (float)(pointCount - 1);
                Vector3 pos = GetBezierPoint(pointA.position, control, pointB.position, t);
                float fade = Mathf.Sin(t * Mathf.PI);
                lines[l].SetPosition(i, pos + lateral * fade);
            }
        }
    }

    // Quadratic Bezier interpolation
    Vector3 GetBezierPoint(Vector3 p0, Vector3 p1, Vector3 p2, float t)
    {
        float u = 1f - t;
        return u * u * p0 + 2f * u * t * p1 + t * t * p2;
    }
}