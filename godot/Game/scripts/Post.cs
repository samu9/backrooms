using Godot;

namespace Backrooms;

/// <summary>
/// The frame pipeline of web/src/post.ts: HDR scene (MSAA) -> two-level bloom -> camcorder grade.
/// The 3D world renders into <see cref="Scene"/>, an HDR SubViewport with no tone mapping. Bloom is
/// a chain of small 2D viewports (bright pass at 1/4, blurred; copied down to 1/8, blurred twice),
/// and the final pass is a full-screen shader on the root viewport.
/// </summary>
public partial class Post : Node
{
    public SubViewport Scene { get; private set; } = null!;
    public ShaderMaterial Final { get; private set; } = null!;
    /// <summary>Render scale of the 3D scene, 0.5..1 (the web's dynamic resolution).</summary>
    public float Scale = 0.85f;

    readonly List<(SubViewport vp, ShaderMaterial mat, int div, Vector2 dir, bool texelOfScene)> passes = [];
    Vector2I window;

    public void Init(int msaa)
    {
        Scene = new SubViewport
        {
            Name = "Scene",
            UseHdr2D = true, // keeps the HDR scene in a float buffer, linear, for the bloom and the grade
            Msaa3D = msaa >= 8 ? Viewport.Msaa.Msaa8X : msaa >= 4 ? Viewport.Msaa.Msaa4X : msaa >= 2 ? Viewport.Msaa.Msaa2X : Viewport.Msaa.Disabled,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        AddChild(Scene);

        var bright = GD.Load<Shader>("res://shaders/post_bright.gdshader");
        var blur = GD.Load<Shader>("res://shaders/post_blur.gdshader");
        Texture2D src = Scene.GetTexture();
        // level 1 (1/4): bright pass, then a horizontal and a vertical blur
        src = Pass(bright, src, 4, Vector2.Zero, texelOfScene: true);
        src = Pass(blur, src, 4, new Vector2(1, 0));
        var b1 = Pass(blur, src, 4, new Vector2(0, 1));
        // level 2 (1/8): copy down, then blur twice
        src = Pass(blur, b1, 8, Vector2.Zero);
        for (int i = 0; i < 2; i++)
        {
            src = Pass(blur, src, 8, new Vector2(1, 0));
            src = Pass(blur, src, 8, new Vector2(0, 1));
        }
        var b2 = src;

        Final = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/post_final.gdshader") };
        Final.SetShaderParameter("t_scene", Scene.GetTexture());
        Final.SetShaderParameter("t_b1", b1);
        Final.SetShaderParameter("t_b2", b2);
        var layer = new CanvasLayer { Name = "Camcorder", Layer = 0 };
        layer.AddChild(new ColorRect { Material = Final, AnchorRight = 1, AnchorBottom = 1, MouseFilter = Control.MouseFilterEnum.Ignore });
        AddChild(layer);
        Resize();
    }

    Texture2D Pass(Shader shader, Texture2D src, int div, Vector2 dir, bool texelOfScene = false)
    {
        var vp = new SubViewport
        {
            Disable3D = true,
            UseHdr2D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        var mat = new ShaderMaterial { Shader = shader };
        mat.SetShaderParameter("t_src", src);
        vp.AddChild(new ColorRect { Material = mat, AnchorRight = 1, AnchorBottom = 1 });
        AddChild(vp);
        passes.Add((vp, mat, div, dir, texelOfScene));
        return vp.GetTexture();
    }

    /// <summary>Sizes every target from the window size and the render scale (web Post.setSize).</summary>
    public void Resize()
    {
        window = DisplayServer.WindowGetSize();
        var sz = new Vector2I(Math.Max(2, (int)(window.X * Scale)), Math.Max(2, (int)(window.Y * Scale)));
        Scene.Size = sz;
        foreach (var (vp, mat, div, dir, texelOfScene) in passes)
        {
            var s = new Vector2I(Math.Max(1, sz.X / div), Math.Max(1, sz.Y / div));
            vp.Size = s;
            // the bright pass steps in scene texels; blurs step in texels of their own size
            if (texelOfScene) mat.SetShaderParameter("texel", new Vector2(1f / sz.X, 1f / sz.Y));
            else mat.SetShaderParameter("dir", new Vector2(dir.X / s.X, dir.Y / s.Y));
        }
        Final.SetShaderParameter("res", new Vector2(sz.X, sz.Y));
    }

    public override void _Process(double delta)
    {
        if (DisplayServer.WindowGetSize() != window) Resize();
    }

    public void Set(string name, float v) => Final.SetShaderParameter(name, v);
    public float Get(string name) => (float)Final.GetShaderParameter(name);
}
