using UnityEngine;

public class StickTipToShader : MonoBehaviour
{
    [SerializeField] private Transform tip; // кончик палки, если не сам этот объект
    [SerializeField] private Renderer cubeRenderer; // рендерер куба желе

    private static readonly int StickTipID = Shader.PropertyToID("_StickTip");
    private MaterialPropertyBlock _mpb;

    private void Awake()
    {
        _mpb = new MaterialPropertyBlock();
    }

    private void Update()
    {
        Vector3 tipPos = tip ? tip.position : transform.position;

        cubeRenderer.GetPropertyBlock(_mpb);
        _mpb.SetVector(StickTipID, tipPos);
        cubeRenderer.SetPropertyBlock(_mpb);
    }
}