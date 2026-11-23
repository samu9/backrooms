using Backrooms.Core;
using Godot;

namespace Backrooms;

/// <summary>The way out (web/src/avatars.ts ExitDoor): a lone door frame standing in the open, white light pouring through it.</summary>
public partial class ExitDoor : Node3D
{
    Avatars avatars = null!;
    ShaderMaterial mat = null!;

    public void Init(Avatars a)
    {
        avatars = a;
        mat = a.LitMaterial();
        var g = new GeoBuilder();
        double[][] frame = [[-0.5, 1.06, 0, 0.08, 2.12, 0.16, 0], [0.5, 1.06, 0, 0.08, 2.12, 0.16, 0], [0, 2.15, 0, 1.08, 0.08, 0.16, 0], [0, 0.01, 0, 1.08, 0.02, 0.16, 0]];
        double[][] light = [[0, 1.06, 0, 0.92, 2.1, 0.02, 0]];
        var id = new Mat4();
        Props.Emit(g, new Template(frame, 2.2, 0.16), id, 0, 0.7, false, [[0.8, 0.79, 0.74]]);
        Props.Emit(g, new Template(light, 2.2, 0.02), id, 4, 0.7, false, [[1.0, 0.97, 0.88]]);
        AddChild(new MeshInstance3D { Mesh = ChunkManager.ToMesh(g), MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        Visible = false;
    }

    public void Show((double X, double Z)? exit)
    {
        if (exit is not { } e)
        {
            Visible = false;
            return;
        }
        Position = new Vector3((float)e.X, 0, (float)e.Z);
        Visible = avatars.Light(mat, e.X, e.Z);
    }
}
