using UnityEngine;
using UnityEngine.InputSystem;

// Simple fly camera for Play mode. Put it on the Main Camera.
//   Look around: hold the RIGHT mouse button (trackpad: two-finger click) and move, OR hold OPTION / ALT
//   and just move the pointer, OR use the ARROW keys.
//   W / A / S / D: forward / left / back / right.   Q / E: down / up.   Shift: faster.
//   Mouse wheel: change speed.   F: jump to a view of the clouds.
//   With Fixed Point on (the default), the camera stays at one spot near the ground and only looks around;
//   F (and the start of Play) points it back at the default view.
public class FlyCamera : MonoBehaviour
{
    [Tooltip("Stand at one fixed point near the ground (no moving, only looking around). F returns to the default view. Off: free flying, and F jumps to a view of the whole cloud box.")]
    public bool fixedPoint = true;
    [Tooltip("Fixed point: angle (degrees) between the direction the sunlight travels and the view direction. 0 = sun straight behind you (flat, front-lit), 180 = looking into the sun (dark, backlit). ~110: the sun is ahead and to the right, out of frame: lit sides, shaded bases, bright edges.")]
    [Range(0, 180)] public float sunAngle = 110f;
    [Tooltip("Fixed point: how far up the view looks (degrees above the horizon). With a 60 degree field of view, 25 keeps a strip of horizon at the bottom and shows the sky up to 55 degrees.")]
    [Range(0, 89)] public float upAngle = 25f;
    [Tooltip("Fixed point: distance from the middle of the cloud box, as a fraction of its width, back from the view direction (so the clouds in the middle are ahead). Stays inside the box: from outside, the box walls cut clouds off.")]
    [Range(0, 0.45f)] public float distance = 0.3f;
    [Tooltip("Fixed point: eye height above the ground (m).")]
    [Min(0)] public float eyeHeight = 2f;

    [Tooltip("Movement speed in Unity units per second (with CloudSim's default World Units Per Km = 1, one unit is 1 km).")]
    [Min(0.01f)] public float speed = 1f;
    [Tooltip("Speed multiplier while Shift is held.")]
    [Min(1)] public float fastMultiplier = 4f;
    [Tooltip("Mouse-look sensitivity (degrees per pixel of mouse movement).")]
    [Min(0.01f)] public float lookSensitivity = 0.15f;
    [Tooltip("Turning speed with the arrow keys (degrees per second).")]
    [Min(1)] public float arrowLookSpeed = 60f;

    float yaw, pitch;

    bool placed;

    void OnEnable() => ReadAngles();

    void ReadAngles()
    {
        var e = transform.eulerAngles;
        yaw = e.y;
        pitch = e.x > 180 ? e.x - 360 : e.x;
    }

    void Update()
    {
        var kb = Keyboard.current;
        var mouse = Mouse.current;
        if (kb == null || mouse == null) return;
        if (fixedPoint && (!placed || kb.fKey.wasPressedThisFrame)) placed = GroundView();

        bool look = mouse.rightButton.isPressed || kb.leftAltKey.isPressed || kb.rightAltKey.isPressed;
        Vector2 d = look ? mouse.delta.ReadValue() * lookSensitivity : Vector2.zero;
        float a = arrowLookSpeed * Time.unscaledDeltaTime;
        if (kb.leftArrowKey.isPressed) d.x -= a;
        if (kb.rightArrowKey.isPressed) d.x += a;
        if (kb.upArrowKey.isPressed) d.y += a;
        if (kb.downArrowKey.isPressed) d.y -= a;
        if (d != Vector2.zero)
        {
            yaw += d.x;
            pitch = Mathf.Clamp(pitch - d.y, -89f, 89f);
            transform.rotation = Quaternion.Euler(pitch, yaw, 0);
        }

        if (fixedPoint)
        {
            // stay put; the point follows the cloud box if the preset (domain size) changes
            if (TryGroundPoint(out var pos, out _)) transform.position = pos;
            return;
        }

        float scroll = mouse.scroll.ReadValue().y;
        if (scroll != 0) speed = Mathf.Clamp(speed * (scroll > 0 ? 1.1f : 1f / 1.1f), 0.01f, 100f);

        Vector3 move = Vector3.zero;
        if (kb.wKey.isPressed) move.z += 1;
        if (kb.sKey.isPressed) move.z -= 1;
        if (kb.dKey.isPressed) move.x += 1;
        if (kb.aKey.isPressed) move.x -= 1;
        if (kb.eKey.isPressed) move.y += 1;
        if (kb.qKey.isPressed) move.y -= 1;
        float v = speed * (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed ? fastMultiplier : 1f);
        Vector3 horizontal = transform.rotation * new Vector3(move.x, 0, move.z);
        transform.position += (horizontal + Vector3.up * move.y) * v * Time.unscaledDeltaTime;

        if (kb.fKey.wasPressedThisFrame) FrameClouds();
    }

    // The fixed point: near the ground inside the cloud box, back from the middle along the view direction. The view
    // direction is set by the sun: Sun Angle away from the direction the sunlight travels.
    bool TryGroundPoint(out Vector3 pos, out Vector3 viewDir)
    {
        pos = transform.position; viewDir = transform.forward;
        var sim = FindAnyObjectByType<CloudSim>();
        if (sim == null) return false;
        Vector3 c = sim.transform.position, s = sim.DomainScale;
        var sun = sim.FindSun();
        Vector3 light = sun != null ? sun.transform.forward : Vector3.forward;
        Vector3 h = new Vector3(light.x, 0, light.z);
        h = h.sqrMagnitude > 1e-6f ? h.normalized : Vector3.forward;
        viewDir = Quaternion.AngleAxis(sunAngle, Vector3.up) * h;
        float metersToUnits = s.y / Mathf.Max(sim.ny * sim.cellSize, 1e-3f);
        pos = c + new Vector3(-viewDir.x * distance * s.x, -0.5f * s.y + eyeHeight * metersToUnits, -viewDir.z * distance * s.z);
        return true;
    }

    bool GroundView()
    {
        if (!TryGroundPoint(out var pos, out var dir)) return false;
        transform.position = pos;
        transform.rotation = Quaternion.LookRotation(dir) * Quaternion.Euler(-upAngle, 0, 0);
        ReadAngles();
        return true;
    }

    // Puts the camera just outside the near side of the cloud box, a little above the cloud layer,
    // looking at the middle of the box.
    void FrameClouds()
    {
        var sim = FindAnyObjectByType<CloudSim>();
        if (sim == null) return;
        Vector3 c = sim.transform.position, s = sim.DomainScale;
        transform.position = c + new Vector3(0, 0.45f * s.y, -0.65f * s.z);
        transform.LookAt(c + new Vector3(0, 0.1f * s.y, 0));
        ReadAngles();
    }
}
