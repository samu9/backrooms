using Backrooms.Core;
using Godot;

namespace Backrooms;

/// <summary>
/// Input and camera for Core.PlayerMotion. Keys are physical (layout-independent), like the web
/// client's e.code: WASD / arrows to walk, Shift to run, N noclip. Click to capture the mouse,
/// Esc to release it.
/// </summary>
public partial class PlayerNode : Node3D
{
    public PlayerMotion Motion = null!;
    public Camera3D Camera { get; private set; } = null!;
    public double Time;
    public MotionResult Last;
    /// <summary>Scripted input for headless runs: hold "forward" for this many more seconds.</summary>
    public double AutoWalk;
    /// <summary>A still frame: no time passes (screenshots, like the web's ?shot).</summary>
    public bool Frozen;
    /// <summary>Off while the menu or the full map has the keyboard.</summary>
    public bool InputEnabled = true;
    /// <summary>The hunt freezes the entity during the countdown.</summary>
    public Func<bool>? Blocked;
    /// <summary>No noclip during a hunt.</summary>
    public Func<bool> NoclipAllowed = () => true;

    public void Init(PlayerMotion motion)
    {
        Motion = motion;
        Camera = new Camera3D { Near = 0.05f, Far = 140f, Fov = (float)motion.BaseFov, Current = true };
        AddChild(Camera);
        Apply(Motion.Step(0, 0, 0, false, 0));
    }

    /// <summary>Called by Main: this node sits in the scene SubViewport, which gets no input of its own.</summary>
    public void HandleInput(InputEvent e)
    {
        if (e is InputEventMouseMotion mm && Input.MouseMode == Input.MouseModeEnum.Captured)
            Motion.Look(mm.Relative.X, mm.Relative.Y);
        else if (e is InputEventKey { Pressed: true, Echo: false } k)
        {
            if (k.PhysicalKeycode == Key.N && NoclipAllowed()) Motion.Noclip = !Motion.Noclip;
            if (k.PhysicalKeycode == Key.Escape) Input.MouseMode = Input.MouseModeEnum.Visible;
        }
    }

    static bool Down(Key a, Key b) => Input.IsPhysicalKeyPressed(a) || Input.IsPhysicalKeyPressed(b);

    public override void _Process(double delta)
    {
        double dt = Frozen ? 0 : Math.Clamp(delta, 0, 0.1);
        Time += dt;
        int ix = 0, iz = 0;
        bool run = false;
        if (InputEnabled)
        {
            if (Down(Key.W, Key.Up)) iz -= 1;
            if (Down(Key.S, Key.Down)) iz += 1;
            if (Down(Key.A, Key.Left)) ix -= 1;
            if (Down(Key.D, Key.Right)) ix += 1;
            run = Input.IsPhysicalKeyPressed(Key.Shift);
        }
        if (Blocked?.Invoke() ?? false) ix = iz = 0;
        if (AutoWalk > 0)
        {
            AutoWalk -= dt;
            iz = -1;
            run = true;
        }
        Apply(Motion.Step(dt, ix, iz, run, Time));
    }

    void Apply(MotionResult r)
    {
        Last = r;
        var p = r.Camera;
        Camera.Position = new Vector3((float)p.X, (float)p.Y, (float)p.Z);
        // Godot's default Euler order is YXZ, the same as the web camera
        Camera.Rotation = new Vector3((float)p.Pitch, (float)p.Yaw, (float)p.Roll);
        Camera.Fov = (float)p.Fov;
    }
}
