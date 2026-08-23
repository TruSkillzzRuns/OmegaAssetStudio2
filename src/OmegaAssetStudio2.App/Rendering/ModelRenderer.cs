using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using SharpGen.Runtime;
using OmegaAssetStudio2.Core.Materials;
using OmegaAssetStudio2.Core.Meshes;
using OmegaAssetStudio2.Core.Textures;
using OmegaAssetStudio2.Core.Workspace;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;

namespace OmegaAssetStudio2.App.Rendering;

/// <summary>One vertex as the graphics card wants it: laid out, not packed.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RenderVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 Uv;

    /// <summary>
    /// The direction the texture's own sideways axis runs in, with the sign of
    /// the third axis in W. A normal map is written against the texture's axes,
    /// so without this there is no way to turn what it says into a direction in
    /// the world.
    /// </summary>
    public Vector4 Tangent;

    public const int Stride =
        (3 * sizeof(float)) + (3 * sizeof(float)) + (2 * sizeof(float)) + (4 * sizeof(float));
}

/// <summary>What the ground needs to draw itself.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SceneConstants
{
    public Matrix4x4 WorldViewProjection;
    public Vector4 LineColour;
    public Vector4 Background;
    public Vector3 Centre;
    public float Reach;

    /// <summary>How far apart the grid lines are, in the model's own units.</summary>
    public float Step;

    /// <summary>0 draws the backdrop, 1 the ground, 2 the beam.</summary>
    public float Mode;

    /// <summary>Which shape the beam takes, from the viewport's own list.</summary>
    public float Style;
    public float Pad1;

    /// <summary>Where the camera is, for the edge-on brightening of the beam.</summary>
    public Vector3 Eye;

    /// <summary>How tall the beam stands.</summary>
    public float Height;

    /// <summary>How long the viewport has been open, for anything that moves.</summary>
    public float Time;

    public float Pad2;
    public float Pad3;
    public float Pad4;

    public const int Size = (16 * 4) + (4 * 4) + (4 * 4) + (4 * 4) + (4 * 4) + (4 * 4) + (4 * 4);
}

/// <summary>
/// Draws a skinned model into a panel, using the graphics card directly.
/// </summary>
/// <remarks>
/// Every failure path here ends with a message rather than an exception. A
/// viewport that cannot start — no suitable card, a driver that refuses the
/// swap chain — must say why in the interface, because the alternative is a
/// black rectangle that gives the user nothing to act on.
/// </remarks>
public sealed class ModelRenderer : IDisposable
{
    /// <summary>
    /// Checks the scene block's hand-written size against the block itself.
    /// </summary>
    /// <remarks>
    /// The same hazard the shading block carries: the count of floats is
    /// written out by hand, it sizes the buffer, and the block is then written
    /// into it whole. Adding a field without raising the count writes past the
    /// buffer, and raising it too far leaves the shader reading a tail nothing
    /// wrote.
    /// </remarks>
    static ModelRenderer()
    {
        int actual = Marshal.SizeOf<SceneConstants>();

        if (actual != SceneConstants.Size)
        {
            throw new InvalidOperationException(
                $"The scene block is {actual} bytes and its stated size is {SceneConstants.Size}. "
                + "SceneConstants.Size has to be raised to match the fields it describes.");
        }
    }

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGISwapChain1? _swapChain;
    private ID3D11RenderTargetView? _backBufferView;
    private ID3D11Texture2D? _depthBuffer;
    private ID3D11DepthStencilView? _depthView;

    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11InputLayout? _inputLayout;
    private ID3D11Buffer? _frameConstants;
    private ID3D11RasterizerState? _rasterState;
    private ID3D11DepthStencilState? _depthState;

    /// <summary>Keeps only the side of a surface that faces the camera.</summary>
    private ID3D11RasterizerState? _solidState;
    private ID3D11SamplerState? _sampler;
    private ID3D11SamplerState? _sharperSampler;

    /// <summary>
    /// The build the model on show came from, which decides how its textures
    /// are sampled and how its frame is assembled.
    /// </summary>
    /// <remarks>
    /// Nothing inside a cooked folder separates one build from another - they
    /// share a folder name, a package format, and the same MaxAnisotropy
    /// buckets in their configuration - so the build is read from the install's
    /// own executable and carried here. An install whose build cannot be read
    /// keeps the sampling and the frame it has always had.
    /// <para>
    /// Both answers are worked out here, once, rather than in the draw loop.
    /// Reading them there parsed three version strings for every part of every
    /// model on every frame, and since the stand's light made the viewport draw
    /// continuously that was thousands of string splits a second for a pair of
    /// values that only change when this is assigned.
    /// </para>
    /// </remarks>
    public string ModelBuild
    {
        get => _modelBuild;
        set
        {
            _modelBuild = value ?? string.Empty;
            _tracedFrame = TracedFrame.Draws(_modelBuild);
            _sharper = Sharper(_modelBuild);
        }
    }

    private string _modelBuild = string.Empty;

    /// <summary>Whether this build's frame is assembled from its own base pass.</summary>
    private bool _tracedFrame;

    /// <summary>Whether this build's models take the sharper sampling.</summary>
    private bool _sharper;

    /// <summary>
    /// The builds whose models are drawn with the sharper sampling.
    /// </summary>
    /// <remarks>
    /// Named one at a time rather than taken as a range. Each was asked for
    /// separately and each was measured against its own install: both ship
    /// MaxAnisotropy buckets of 0, 0, 2, 4 and 8, and neither asks anywhere for
    /// a negative mip bias. A build not on this list samples exactly as it
    /// always has.
    /// </remarks>
    private static readonly string[] SharperFrom = ["1.53.0.203", "1.48.0.1712", "1.52.0.1700"];

    private ID3D11SamplerState? Sampling => _sharper ? _sharperSampler ?? _sampler : _sampler;

    /// <summary>Whether this build is one of the ones asked for.</summary>
    private static bool Sharper(string build)
    {
        foreach (string wanted in SharperFrom)
        {
            if (GameClient.Reads(build, wanted)) return true;
        }

        return false;
    }

    private ID3D11Buffer? _vertexBuffer;
    private ID3D11Buffer? _indexBuffer;
    private int _indexCount;

    /// <summary>
    /// One run of triangles: its pictures, which channel of its mask means
    /// what, and the material's own account of how it shades.
    /// </summary>
    private sealed record DrawPart
    {
        public required int BaseIndex { get; init; }
        public required int IndexCount { get; init; }

        public ID3D11ShaderResourceView? Surface { get; init; }
        public ID3D11ShaderResourceView? Mask { get; init; }
        public ID3D11ShaderResourceView? Environment { get; init; }
        public ID3D11ShaderResourceView? Normal { get; init; }
        public ID3D11ShaderResourceView? Tint { get; init; }
        public ID3D11ShaderResourceView? Ramp { get; init; }

        public Vector4 GlossSelect { get; init; }
        public Vector4 ReflectSelect { get; init; }
        public Vector4 SharpSelect { get; init; }
        public Vector4 RimSelect { get; init; }

        public int EnvironmentLevels { get; init; }

        public SurfaceShading Shading { get; init; } = SurfaceShading.Plain;

        /// <summary>Whether this run's material asks for both sides.</summary>
        public bool TwoSided => Shading.TwoSided;
    }

    /// <summary>The runs of triangles to draw, each with its own picture.</summary>
    private readonly List<DrawPart> _parts = [];

    /// <summary>Pictures uploaded for the current model, owned and released here.</summary>
    private readonly List<ID3D11ShaderResourceView> _surfaces = [];

    private ID3D11VertexShader? _sceneVertexShader;
    private ID3D11PixelShader? _scenePixelShader;
    private ID3D11InputLayout? _sceneLayout;
    private ID3D11Buffer? _sceneConstants;
    private ID3D11Buffer? _floorBuffer;
    private int _floorVertexCount;
    private ID3D11Buffer? _backdropBuffer;

    /// <summary>How far apart the ground's grid lines are.</summary>
    private float _floorStep;

    private ID3D11Buffer? _standVertexBuffer;
    private ID3D11Buffer? _standIndexBuffer;
    private int _standIndexCount;

    /// <summary>
    /// The stand's own pictures, kept apart from the model's so that changing
    /// costume does not release them.
    /// </summary>
    private readonly List<ID3D11Texture2D> _standTextures = [];
    private readonly List<ID3D11ShaderResourceView> _standSurfaces = [];

    private ID3D11ShaderResourceView? _standColour;
    private ID3D11ShaderResourceView? _standNormal;
    private ID3D11ShaderResourceView? _standMask;
    private Vector4 _standGloss;
    private Vector4 _standReflect;

    /// <summary>Where the stand sits, worked out from the model standing on it.</summary>
    private Matrix4x4 _standTransform = Matrix4x4.Identity;

    /// <summary>Where the model meets the stand, and how wide it is there.</summary>
    private Vector4 _standingAt = new(0f, 0f, 0f, 100f);

    /// <summary>How long the viewport has been open, for anything that moves.</summary>
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>
    /// Whether the stand throws light up around what is standing on it.
    /// </summary>
    /// <remarks>
    /// The viewport's own furniture rather than anything the game asks for, so
    /// it is drawn on the stand alone and never on the model.
    /// </remarks>
    /// <summary>What the stand throws up around what is standing on it.</summary>
    public enum Projection
    {
        /// <summary>Nothing at all: the stand is only a stand.</summary>
        None,

        /// <summary>A cone of haze, opening as it climbs.</summary>
        Beam,

        /// <summary>Rings climbing the cone, as a scanner sweeps.</summary>
        Rings,

        /// <summary>One bright band running up and starting again.</summary>
        Scan,

        /// <summary>Uprights and cross-pieces, like something held in a field.</summary>
        Cage,

        /// <summary>Straight-sided and even, a pillar of light rather than a cone.</summary>
        Column,

        /// <summary>One line winding up and round, turning slowly.</summary>
        Spiral,

        /// <summary>Two sets of lines crossing, a net drawn in light.</summary>
        Lattice,

        /// <summary>Specks carried upward and starting again at the pad.</summary>
        Motes,

        /// <summary>Slices thrown bright for a moment, the rest left low.</summary>
        Glitch,

        /// <summary>Cloud folding through the beam, with no edges in it.</summary>
        Plasma,

        /// <summary>Sparks carried up and going out as they rise.</summary>
        Embers,

        /// <summary>Breaking apart and coming back, as though still resolving.</summary>
        Dissolve,

        /// <summary>A shell over the model rather than a beam under it.</summary>
        Dome,
    }

    /// <summary>Which of them is drawn.</summary>
    public Projection Projects { get; set; } = Projection.Beam;

    /// <summary>Whether anything is thrown at all.</summary>
    public bool StandProjects => Projects != Projection.None;

    /// <summary>
    /// How far the model is lifted clear of what it stands on, as a share of
    /// its own height.
    /// </summary>
    /// <remarks>
    /// Small on purpose. Enough that the beam shows underneath the feet and the
    /// model reads as held in the light rather than set down on the pad, and
    /// not so much that it looks like it is falling.
    /// </remarks>
    public float StandLift { get; set; } = 0.12f;

    /// <summary>How far the model is lifted, in the model's own units.</summary>
    private float _lift;

    private ID3D11Buffer? _beamBuffer;
    private int _beamVertexCount;
    private Vector3 _beamFoot;
    private float _beamHeight;
    private float _beamReach;

    /// <summary>Adds what is drawn to what is already there, rather than replacing it.</summary>
    private ID3D11BlendState? _addingBlend;

    /// <summary>Tests depth without writing it, so haze does not hide what follows.</summary>
    private ID3D11DepthStencilState? _readDepth;

    /// <summary>Kept so the stand can be re-fitted when the model changes.</summary>
    private PedestalMesh? _stand;
    private Vector3 _standSize;

    /// <summary>
    /// The lowest point of the model actually being drawn.
    /// </summary>
    /// <remarks>
    /// Taken from its vertices rather than from the bounds it declares. Those
    /// bounds are drawn a little wider than the model on every side - measured
    /// across four costumes, their floor sits 4.6 to 5.6 units below the lowest
    /// vertex - and a stand put under the declared floor leaves that much air
    /// under the model's feet.
    /// </remarks>
    private float _modelFloor;

    /// <summary>True while a stand is loaded, so the bare grid stands down.</summary>
    public bool HasStand => _standIndexCount > 0;

    /// <summary>Where the model stands, so the ground can be put under it.</summary>
    private Vector3 _floorCentre;
    private float _floorReach;
    private readonly List<ID3D11Texture2D> _surfaceTextures = [];

    private SwapChainPanel? _panel;
    private int _width;
    private int _height;

    /// <summary>Why the viewport is not drawing, when it is not.</summary>
    public string? Problem { get; private set; }

    public bool Ready => _device is not null && _swapChain is not null;

    /// <summary>True once a model has been handed over and can be drawn.</summary>
    public bool HasModel => _indexCount > 0;

    public OrbitCamera Camera { get; } = new();

    /// <summary>Colour used where a model has no texture bound yet.</summary>
    public Vector3 BaseColour { get; set; } = new(0.62f, 0.64f, 0.68f);

    /// <summary>
    /// Whether surfaces are drawn with their pictures. Turning this off shows
    /// the shape alone, which is how a model's geometry is judged separately
    /// from its paint.
    /// </summary>
    public bool ShowTextures { get; set; } = true;

    /// <summary>How many of the model's parts have a picture bound.</summary>
    public int TexturedPartCount => _parts.Count(p => p.Surface is not null);

    /// <summary>How many parts the model is drawn in.</summary>
    public int PartCount => _parts.Count;

    /// <summary>
    /// Starts the renderer against a panel. Returns false and sets
    /// <see cref="Problem"/> rather than throwing, so the page can show the
    /// reason next to an otherwise empty viewport.
    /// </summary>
    public bool Attach(SwapChainPanel panel, int width, int height)
    {
        _panel = panel;
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);

        try
        {
            CreateDevice();
            CreatePipeline();
            CreateSwapChain();
            CreateRenderTargets();

            Problem = null;
            return true;
        }
        catch (Exception ex)
        {
            Problem = $"The 3D viewport could not start: {ex.Message}";
            return false;
        }
    }

    private void CreateDevice()
    {
        // A hardware device first; software rendering if the machine has no
        // usable card, which is slow but still shows the model.
        DeviceCreationFlags flags = DeviceCreationFlags.BgraSupport;
        FeatureLevel[] levels = [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];

        Result result = D3D11CreateDevice(
            null, DriverType.Hardware, flags, levels, out ID3D11Device? device, out ID3D11DeviceContext? context);

        if (result.Failure)
        {
            result = D3D11CreateDevice(
                null, DriverType.Warp, flags, levels, out device, out context);
        }

        result.CheckError();

        _device = device;
        _context = context;
    }

    private void CreatePipeline()
    {
        ID3D11Device device = _device!;

        Compiler.Compile(
            ModelShaders.Source, ModelShaders.VertexEntryPoint, "model.hlsl",
            ModelShaders.VertexProfile, out Blob vertexCode, out Blob? vertexErrors);

        using (vertexErrors)
        {
            if (vertexCode is null)
                throw new InvalidOperationException(vertexErrors?.AsString() ?? "the vertex shader did not compile.");
        }

        Compiler.Compile(
            ModelShaders.Source, ModelShaders.PixelEntryPoint, "model.hlsl",
            ModelShaders.PixelProfile, out Blob pixelCode, out Blob? pixelErrors);

        using (pixelErrors)
        {
            if (pixelCode is null)
                throw new InvalidOperationException(pixelErrors?.AsString() ?? "the pixel shader did not compile.");
        }

        using (vertexCode)
        using (pixelCode)
        {
            _vertexShader = device.CreateVertexShader(vertexCode.AsSpan());
            _pixelShader = device.CreatePixelShader(pixelCode.AsSpan());

            InputElementDescription[] elements =
            [
                new("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
                new("TEXCOORD", 0, Format.R32G32_Float, 24, 0),
                new("TANGENT", 0, Format.R32G32B32A32_Float, 32, 0),
            ];

            _inputLayout = device.CreateInputLayout(elements, vertexCode.AsSpan());
        }

        _frameConstants = device.CreateBuffer(
            FrameConstants.Size, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);

        CreateScenePipeline(device);

        // Models are wound for the game's own convention, so nothing is culled
        // here. A back face that shows through is far less confusing than a
        // model that renders inside out with half its surface missing.
        _rasterState = device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            // Both sides drawn. Used only for the materials that ask for it;
            // everything else is drawn with the state below, which keeps only
            // the side facing the camera.
            CullMode = CullMode.None,
            FrontCounterClockwise = false,
            DepthClipEnable = true,
        });

        // The ordinary state: only the side of a surface that faces the camera.
        //
        // Drawing both sides of everything was putting two copies of a cape at
        // exactly the same depth. Some capes are built twice over - every one
        // of the 3,460 triangles in one costume's cape and all 1,396 in
        // another's has a twin in the same place wound the other way, which is
        // how an artist makes a sheet that can be seen from behind. With both
        // copies drawn neither wins the depth test cleanly and the cape came out
        // as a patchwork of red and pale lining.
        //
        // Which winding to keep is measured rather than reasoned about: with
        // this setting a triangle whose winding turns away from the camera is
        // kept, and 99.9% of thirteen million triangles in the game's models are
        // wound opposite to the direction their surface faces. So the side whose
        // surface faces the camera is the side that survives.
        _solidState = device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.Back,
            FrontCounterClockwise = false,
            DepthClipEnable = true,
        });

        _depthState = device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = DepthWriteMask.All,
            DepthFunc = ComparisonFunction.Less,
        });

        // What every build but one is drawn with, unchanged.
        // Added to what is already drawn. Light arriving, not paint over it.
        var adding = new BlendDescription();
        adding.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = Blend.SourceAlpha,
            DestinationBlend = Blend.One,
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One,
            DestinationBlendAlpha = Blend.One,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All,
        };

        _addingBlend = device.CreateBlendState(adding);

        // Tested against what is in front, but not written - haze that wrote
        // depth would hide everything drawn after it.
        _readDepth = device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthFunc = ComparisonFunction.Less,
        });

        _sampler = device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            MaxLOD = float.MaxValue,
        });

        // And what 1.53.0.203's models are drawn with.
        //
        // Anisotropic, at the eight that build's own top quality bucket asks
        // for: DefaultSystemSettings.ini sets MaxAnisotropy across its five
        // buckets to 0, 0, 2, 4 and 8, so sixteen is not a setting the client
        // offers. A model turned away from the camera loses its panel lines to
        // plain linear filtering long before then.
        //
        // No mip bias. The client biases character textures the other way -
        // TEXTUREGROUP_Character carries LODBias=1, so it drops a level - and
        // nothing in its configuration asks for a negative one. The viewport
        // already shows the top level, which is as sharp as the data goes.
        _sharperSampler = device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.Anisotropic,
            MaxAnisotropy = 8,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            MipLODBias = 0f,
            MaxLOD = float.MaxValue,
        });
    }

    private void CreateSwapChain()
    {
        using IDXGIDevice dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
        using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
        using IDXGIFactory2 factory = adapter.GetParent<IDXGIFactory2>();

        var description = new SwapChainDescription1
        {
            Width = (uint)_width,
            Height = (uint)_height,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = Vortice.DXGI.AlphaMode.Premultiplied,
        };

        _swapChain = factory.CreateSwapChainForComposition(_device!, description);

        BindToPanel();
    }

    /// <summary>
    /// Hands the swap chain to the panel. The panel exposes this only through a
    /// native interface, so it has to be asked for by hand.
    /// </summary>
    private void BindToPanel()
    {
        IntPtr panelUnknown = Marshal.GetIUnknownForObject(_panel!);
        IntPtr native = IntPtr.Zero;

        try
        {
            Guid iid = typeof(ISwapChainPanelNative).GUID;

            int hr = Marshal.QueryInterface(panelUnknown, ref iid, out native);
            if (hr < 0)
                throw new InvalidOperationException($"the panel would not accept a swap chain (0x{hr:X8}).");

            var panelNative = (ISwapChainPanelNative)Marshal.GetObjectForIUnknown(native);
            panelNative.SetSwapChain(_swapChain!.NativePointer);
        }
        finally
        {
            if (native != IntPtr.Zero) Marshal.Release(native);
            if (panelUnknown != IntPtr.Zero) Marshal.Release(panelUnknown);
        }

        _context!.Flush();
    }

    private void CreateRenderTargets()
    {
        using ID3D11Texture2D backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        _backBufferView = _device!.CreateRenderTargetView(backBuffer);

        _depthBuffer = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_width,
            Height = (uint)_height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.D32_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.DepthStencil,
        });

        _depthView = _device.CreateDepthStencilView(_depthBuffer);
    }

    private void ReleaseRenderTargets()
    {
        _backBufferView?.Dispose();
        _backBufferView = null;
        _depthView?.Dispose();
        _depthView = null;
        _depthBuffer?.Dispose();
        _depthBuffer = null;
    }

    /// <summary>Follows the panel when it changes size.</summary>
    public void Resize(int width, int height)
    {
        if (!Ready) return;

        width = Math.Max(1, width);
        height = Math.Max(1, height);

        if (width == _width && height == _height) return;

        _width = width;
        _height = height;

        ReleaseRenderTargets();

        _swapChain!.ResizeBuffers(2, (uint)_width, (uint)_height, Format.B8G8R8A8_UNorm, SwapChainFlags.None);

        CreateRenderTargets();
    }

    /// <summary>
    /// Hands over a level of detail to draw, replacing whatever was there.
    /// </summary>
    /// <param name="surfaces">
    /// The picture for each material slot. Slots without one are drawn in the
    /// plain colour rather than skipped, so a partly-textured model still shows
    /// all of itself.
    /// </param>
    public void SetModel(SkeletalMeshLod lod, IReadOnlyList<MeshSurface>? surfaces = null)
    {
        ReleaseModel();

        if (!Ready || !lod.HasGeometry || lod.Indices.Count == 0) return;

        int count = lod.Positions.Count;
        var vertices = new RenderVertex[count];

        for (int i = 0; i < count; i++)
        {
            vertices[i] = new RenderVertex
            {
                Position = lod.Positions[i],
                Normal = i < lod.Normals.Count ? lod.Normals[i] : Vector3.UnitZ,
                Uv = i < lod.TexCoords.Count ? lod.TexCoords[i] : Vector2.Zero,

                // Read from the tangent frame the file stores, not worked out
                // from the triangles. Where a mesh carries none, a direction
                // square to the normal stands in and the normal map simply has
                // no effect on that vertex.
                Tangent = i < lod.Tangents.Count
                    ? lod.Tangents[i]
                    : new Vector4(Vector3.UnitX, 1f),
            };
        }

        // Indices arrive as whole numbers whatever width the file stored them
        // at; the card is given the wider form so both cases draw the same.
        var indices = new uint[lod.Indices.Count];
        for (int i = 0; i < indices.Length; i++)
        {
            int index = lod.Indices[i];
            indices[i] = index >= 0 && index < count ? (uint)index : 0u;
        }

        _vertexBuffer = _device!.CreateBuffer(vertices, BindFlags.VertexBuffer);
        _indexBuffer = _device.CreateBuffer(indices, BindFlags.IndexBuffer);
        _indexCount = indices.Length;

        float lowest = float.MaxValue;
        foreach (RenderVertex vertex in vertices) lowest = MathF.Min(lowest, vertex.Position.Z);
        _modelFloor = lowest < float.MaxValue ? lowest : 0f;

        BuildParts(lod, surfaces ?? []);
    }

    /// <summary>
    /// Uploads each material slot's picture and works out which run of triangles
    /// uses it.
    /// </summary>
    private void BuildParts(SkeletalMeshLod lod, IReadOnlyList<MeshSurface> surfaces)
    {
        var bySlot = new Dictionary<int, ID3D11ShaderResourceView>();
        var specBySlot = new Dictionary<int, ID3D11ShaderResourceView>();
        var envBySlot = new Dictionary<int, ID3D11ShaderResourceView>();
        var normalBySlot = new Dictionary<int, ID3D11ShaderResourceView>();
        var envLevelsBySlot = new Dictionary<int, int>();
        var tintBySlot = new Dictionary<int, ID3D11ShaderResourceView>();
        var rampBySlot = new Dictionary<int, ID3D11ShaderResourceView>();
        var glossBySlot = new Dictionary<int, Vector4>();
        var reflectBySlot = new Dictionary<int, Vector4>();
        var sharpBySlot = new Dictionary<int, Vector4>();
        var rimBySlot = new Dictionary<int, Vector4>();
        var shadingBySlot = new Dictionary<int, SurfaceShading>();

        foreach (MeshSurface surface in surfaces)
        {
            ID3D11ShaderResourceView? view = TryUpload(surface);
            if (view is not null) bySlot[surface.MaterialIndex] = view;

            // How this material says it shades: which terms it uses at all, and
            // with what numbers and colours. Every one of these came out of the
            // material's own parameters and the choices it was compiled with.
            shadingBySlot[surface.MaterialIndex] = SurfaceShadingReader.Read(surface.Settings, surface.UsesSpecularColour, surface.Given);

            // The other two maps are gamma-free data rather than pictures, so
            // they go up linear - reading a specular map as though it were
            // colour makes everything shinier than it is.
            // The packed mask, not the specular colour. The colour slot is a
            // flat white texture on most costumes, so reading gloss from it
            // made every surface a mirror.
            if (surface.Mask is not null)
            {
                ID3D11ShaderResourceView? mask = TryUploadImage(surface.Mask, isSrgb: false);

                if (mask is not null)
                {
                    specBySlot[surface.MaterialIndex] = mask;
                    glossBySlot[surface.MaterialIndex] = Selector(surface.GlossChannel);
                    reflectBySlot[surface.MaterialIndex] = Selector(surface.ReflectChannel);
                    sharpBySlot[surface.MaterialIndex] = Selector(surface.SharpnessChannel);
                    rimBySlot[surface.MaterialIndex] = Selector(surface.RimChannel);
                }
            }

            if (surface.Environment is not null)
            {
                // Blurred here rather than by the card. The card averages square
                // blocks of the picture, which is the wrong average for a
                // panorama: a row near the top covers a sliver of the
                // surroundings and a row across the middle covers a band. On
                // the game's own panoramas the two answers differ by as much as
                // 67 of 255.
                IReadOnlyList<TextureImage> levels = EnvironmentPrefilter.Build(surface.Environment);

                ID3D11ShaderResourceView? env = TryUploadLevels(levels, isSrgb: true);

                if (env is not null)
                {
                    envBySlot[surface.MaterialIndex] = env;
                    envLevelsBySlot[surface.MaterialIndex] = levels.Count;
                }
            }

            if (surface.NormalMap is not null)
            {
                ID3D11ShaderResourceView? bump = TryUploadImage(surface.NormalMap, isSrgb: false);
                if (bump is not null) normalBySlot[surface.MaterialIndex] = bump;
            }

            // The colour of the highlight is a picture, not data, so it goes up
            // gamma-encoded like the surface colour does.
            if (surface.Specular is not null)
            {
                ID3D11ShaderResourceView? tint = TryUploadImage(surface.Specular, isSrgb: true);
                if (tint is not null) tintBySlot[surface.MaterialIndex] = tint;
            }

            // A curve of light, so it goes up gamma-encoded like a colour.
            if (surface.Ramp is not null)
            {
                ID3D11ShaderResourceView? ramp = TryUploadImage(surface.Ramp, isSrgb: true);
                if (ramp is not null) rampBySlot[surface.MaterialIndex] = ramp;
            }
        }

        foreach (MeshSection section in lod.Sections)
        {
            if (section.IndexCount <= 0) continue;

            // A section that runs past the buffer would take the draw call with
            // it, so it is trimmed rather than trusted.
            int start = Math.Clamp(section.BaseIndex, 0, _indexCount);
            int length = Math.Clamp(section.IndexCount, 0, _indexCount - start);
            if (length <= 0) continue;

            _parts.Add(new DrawPart
            {
                BaseIndex = start,
                IndexCount = length,
                Surface = bySlot.GetValueOrDefault(section.MaterialIndex),
                Mask = specBySlot.GetValueOrDefault(section.MaterialIndex),
                Environment = envBySlot.GetValueOrDefault(section.MaterialIndex),
                Normal = normalBySlot.GetValueOrDefault(section.MaterialIndex),
                Tint = tintBySlot.GetValueOrDefault(section.MaterialIndex),
                Ramp = rampBySlot.GetValueOrDefault(section.MaterialIndex),
                GlossSelect = glossBySlot.GetValueOrDefault(section.MaterialIndex),
                ReflectSelect = reflectBySlot.GetValueOrDefault(section.MaterialIndex),
                SharpSelect = sharpBySlot.GetValueOrDefault(section.MaterialIndex),
                RimSelect = rimBySlot.GetValueOrDefault(section.MaterialIndex),
                EnvironmentLevels = envLevelsBySlot.GetValueOrDefault(section.MaterialIndex),
                Shading = shadingBySlot.GetValueOrDefault(section.MaterialIndex, SurfaceShading.Plain),
            });
        }

        // A model whose sections do not describe it — none listed, or all of
        // them empty — is still drawn, as one piece.
        if (_parts.Count == 0)
            _parts.Add(new DrawPart
            {
                BaseIndex = 0,
                IndexCount = _indexCount,
                Surface = bySlot.Count > 0 ? bySlot.Values.First() : null,
                Mask = specBySlot.Count > 0 ? specBySlot.Values.First() : null,
                Environment = envBySlot.Count > 0 ? envBySlot.Values.First() : null,
                Normal = normalBySlot.Count > 0 ? normalBySlot.Values.First() : null,
                Tint = tintBySlot.Count > 0 ? tintBySlot.Values.First() : null,
                Ramp = rampBySlot.Count > 0 ? rampBySlot.Values.First() : null,
                GlossSelect = glossBySlot.Count > 0 ? glossBySlot.Values.First() : default,
                ReflectSelect = reflectBySlot.Count > 0 ? reflectBySlot.Values.First() : default,
                SharpSelect = sharpBySlot.Count > 0 ? sharpBySlot.Values.First() : default,
                RimSelect = rimBySlot.Count > 0 ? rimBySlot.Values.First() : default,
                EnvironmentLevels = envLevelsBySlot.Count > 0 ? envLevelsBySlot.Values.First() : 0,
                Shading = shadingBySlot.Count > 0 ? shadingBySlot.Values.First() : SurfaceShading.Plain,
            });
    }

    private ID3D11ShaderResourceView? TryUpload(MeshSurface surface)
        => TryUploadImage(surface.Image, surface.IsSrgb);

    /// <summary>
    /// Puts one picture on the card, with a mip chain built for it. Whether it
    /// is treated as colour or as data is the caller's to say: a specular map
    /// read as colour comes out brighter than it is, and a colour map read as
    /// data comes out washed.
    /// </summary>
    private ID3D11ShaderResourceView? TryUploadImage(TextureImage image, bool isSrgb) =>
        TryUploadImage(image, isSrgb, _surfaceTextures, _surfaces);

    /// <summary>
    /// Puts a picture on the card with the levels it was given, rather than
    /// letting the card make its own.
    /// </summary>
    private ID3D11ShaderResourceView? TryUploadLevels(IReadOnlyList<TextureImage> levels, bool isSrgb)
    {
        if (levels.Count == 0) return null;

        TextureImage first = levels[0];
        if (first.Width <= 0 || first.Height <= 0) return null;

        try
        {
            var boxes = new SubresourceData[levels.Count];
            var pinned = new List<GCHandle>(levels.Count);

            try
            {
                for (int i = 0; i < levels.Count; i++)
                {
                    GCHandle handle = GCHandle.Alloc(levels[i].Rgba, GCHandleType.Pinned);
                    pinned.Add(handle);

                    boxes[i] = new SubresourceData(handle.AddrOfPinnedObject(), (uint)(levels[i].Width * 4));
                }

                ID3D11Texture2D texture = _device!.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)first.Width,
                    Height = (uint)first.Height,
                    MipLevels = (uint)levels.Count,
                    ArraySize = 1,
                    Format = isSrgb ? Format.R8G8B8A8_UNorm_SRgb : Format.R8G8B8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource,
                }, boxes);

                ID3D11ShaderResourceView view = _device.CreateShaderResourceView(texture);

                _surfaceTextures.Add(texture);
                _surfaces.Add(view);

                return view;
            }
            finally
            {
                foreach (GCHandle handle in pinned) handle.Free();
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// As above, into a named pool. A stand and the model standing on it are
    /// loaded and dropped independently, so their pictures cannot share one
    /// pool - changing costume would otherwise release the stand's textures.
    /// </summary>
    private ID3D11ShaderResourceView? TryUploadImage(
        TextureImage image, bool isSrgb,
        List<ID3D11Texture2D> textures, List<ID3D11ShaderResourceView> views)
    {
        if (image.Width <= 0 || image.Height <= 0) return null;
        if (image.Rgba.Length < image.Width * image.Height * 4) return null;

        try
        {
            // Gamma-encoded pictures are declared as such so the card converts
            // them on the way in; treating them as linear washes the colour out.
            Format format = isSrgb
                ? Format.R8G8B8A8_UNorm_SRgb
                : Format.R8G8B8A8_UNorm;

            // Made with room for a full mip chain and generated on the card.
            // Without it, a model seen from any distance shimmers as the
            // surface aliases against the pixels.
            ID3D11Texture2D texture = _device!.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)image.Width,
                Height = (uint)image.Height,
                MipLevels = 0,
                ArraySize = 1,
                Format = format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                MiscFlags = ResourceOptionFlags.GenerateMips,
            });

            _context!.UpdateSubresource(image.Rgba, texture, 0, (uint)(image.Width * 4));

            ID3D11ShaderResourceView view = _device.CreateShaderResourceView(texture);
            _context.GenerateMips(view);

            textures.Add(texture);
            views.Add(view);

            return view;
        }
        catch (Exception)
        {
            // A picture that will not upload costs this one surface its colour,
            // not the whole model.
            return null;
        }
    }

    /// <summary>
    /// Turns a channel number into a vector that picks it out of a sample.
    /// Nothing selected where the material never named that quantity, which
    /// leaves the surface plain rather than guessing at a channel.
    /// </summary>
    /// <summary>
    /// Turns a colour picked by eye into the linear light the shaders work in.
    /// </summary>
    /// <remarks>
    /// Every constant colour here - the ground, its grid, the grey a model is
    /// drawn in before its textures land - was chosen by looking at the screen,
    /// so it is a display value. The shaders now work in linear and encode on
    /// the way out, so those constants have to be converted going in or they
    /// come back noticeably lighter than they were picked to be.
    /// </remarks>
    private static Vector4 Linear(Vector4 shown) => new(
        MathF.Pow(shown.X, 2.2f), MathF.Pow(shown.Y, 2.2f), MathF.Pow(shown.Z, 2.2f), shown.W);

    private static Vector3 Linear(Vector3 shown) => new(
        MathF.Pow(shown.X, 2.2f), MathF.Pow(shown.Y, 2.2f), MathF.Pow(shown.Z, 2.2f));

    private static Vector4 Selector(int channel) => channel switch
    {
        0 => new Vector4(1f, 0f, 0f, 0f),
        1 => new Vector4(0f, 1f, 0f, 0f),
        2 => new Vector4(0f, 0f, 1f, 0f),
        3 => new Vector4(0f, 0f, 0f, 1f),
        _ => Vector4.Zero,
    };

    /// <summary>Compiles the shaders the ground is drawn with.</summary>
    private void CreateScenePipeline(ID3D11Device device)
    {
        Compiler.Compile(
            ModelShaders.SceneSource, ModelShaders.VertexEntryPoint, "scene.hlsl",
            ModelShaders.VertexProfile, out Blob vertexCode, out Blob? vertexErrors);

        using (vertexErrors)
        {
            if (vertexCode is null) return;
        }

        Compiler.Compile(
            ModelShaders.SceneSource, ModelShaders.PixelEntryPoint, "scene.hlsl",
            ModelShaders.PixelProfile, out Blob pixelCode, out Blob? pixelErrors);

        using (pixelErrors)
        {
            if (pixelCode is null) { vertexCode.Dispose(); return; }
        }

        using (vertexCode)
        using (pixelCode)
        {
            _sceneVertexShader = device.CreateVertexShader(vertexCode.AsSpan());
            _scenePixelShader = device.CreatePixelShader(pixelCode.AsSpan());

            _sceneLayout = device.CreateInputLayout(
                [new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0)],
                vertexCode.AsSpan());
        }

        _sceneConstants = device.CreateBuffer(
            SceneConstants.Size, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);

        // One oversized triangle covering the screen, given straight to the
        // shader in screen space. It never changes, so it is built once.
        Vector3[] backdrop =
        [
            new(-1f, -3f, 0f),
            new(-1f,  1f, 0f),
            new( 3f,  1f, 0f),
        ];

        _backdropBuffer = device.CreateBuffer<Vector3>(backdrop, BindFlags.VertexBuffer);
    }

    /// <summary>
    /// Lays a grid under the model, sized to it.
    /// </summary>
    /// <remarks>
    /// The spacing is chosen so the grid always reads as about twenty squares
    /// across whatever the model's size - a costume and a hammer are wildly
    /// different scales, and a grid fixed in world units would be either
    /// invisible under one or a solid block under the other.
    /// </remarks>
    private void BuildFloor(MeshBounds bounds)
    {
        _floorBuffer?.Dispose();
        _floorBuffer = null;
        _floorVertexCount = 0;

        if (_device is null) return;

        float reach = MathF.Max(bounds.Radius, 1f) * 2.6f;
        float step = reach / 9f;
        float floor = bounds.OriginZ - bounds.ExtentZ;

        _floorCentre = new Vector3(bounds.OriginX, bounds.OriginY, floor);
        _floorReach = reach;
        _floorStep = step;

        // A filled square, faded to a disc by the shader. Filled rather than
        // ruled: the grid is drawn onto it, and a surface that catches light is
        // what makes it read as ground.
        Vector3 Corner(float x, float y) =>
            new(bounds.OriginX + (x * reach), bounds.OriginY + (y * reach), floor);

        Vector3[] quad =
        [
            Corner(-1f, -1f), Corner(-1f, 1f), Corner(1f, 1f),
            Corner(-1f, -1f), Corner(1f, 1f), Corner(1f, -1f),
        ];

        _floorBuffer = _device.CreateBuffer<Vector3>(quad, BindFlags.VertexBuffer);

        BuildBeam(bounds, floor);
        _floorVertexCount = quad.Length;
    }

    /// <summary>Draws the ground, before the model so it never hides it.</summary>
    /// <summary>
    /// The sleeve of light the stand throws up around what stands on it.
    /// </summary>
    /// <remarks>
    /// An open cylinder with no ends, wide enough to clear the model and tall
    /// enough to pass its head. Nothing but geometry to hang the haze on: what
    /// makes it read as a beam is the shading, which brightens where the sleeve
    /// turns away from the viewer and fades as it rises.
    /// </remarks>
    private void BuildBeam(MeshBounds bounds, float floor)
    {
        _beamBuffer?.Dispose();
        _beamBuffer = null;
        _beamVertexCount = 0;

        if (_device is null) return;

        const int Around = 64;

        // Narrow where it leaves the pad and opening out as it climbs, which is
        // what a projector throws: close in around the feet, wide enough by the
        // top to have passed the head and shoulders.
        float wide = MathF.Max(bounds.Radius, 1f);

        _beamReach = wide * 0.55f;
        _beamHeight = MathF.Max(bounds.Height, 1f) * 1.25f;
        _beamFoot = new Vector3(bounds.OriginX, bounds.OriginY, floor);

        float head = Projects == Projection.Column ? _beamReach : wide * 1.35f;

        Vector3 At(float angle, float radius, float height) => new(
            _beamFoot.X + (MathF.Cos(angle) * radius),
            _beamFoot.Y + (MathF.Sin(angle) * radius),
            floor + height);

        // A shell rather than a sleeve: wide enough and tall enough to cover
        // the model, closed over the top.
        if (Projects == Projection.Dome)
        {
            const int Up = 20;

            _beamReach = MathF.Max(wide * 1.15f, 1f);
            _beamHeight = MathF.Max(bounds.Height * 1.08f, 1f);

            var shell = new Vector3[Around * Up * 6];
            int put = 0;

            for (int ring = 0; ring < Up; ring++)
            {
                float low = ring / (float)Up * MathF.PI * 0.5f;
                float high = (ring + 1) / (float)Up * MathF.PI * 0.5f;

                Vector3 On(float angle, float lift) => At(
                    angle,
                    _beamReach * MathF.Cos(lift),
                    _beamHeight * MathF.Sin(lift));

                for (int i = 0; i < Around; i++)
                {
                    float from = i / (float)Around * MathF.Tau;
                    float to = (i + 1) / (float)Around * MathF.Tau;

                    shell[put++] = On(from, low);
                    shell[put++] = On(from, high);
                    shell[put++] = On(to, high);
                    shell[put++] = On(from, low);
                    shell[put++] = On(to, high);
                    shell[put++] = On(to, low);
                }
            }

            _beamBuffer = _device.CreateBuffer<Vector3>(shell, BindFlags.VertexBuffer);
            _beamVertexCount = shell.Length;
            return;
        }

        var sleeve = new Vector3[Around * 6];

        for (int i = 0; i < Around; i++)
        {
            float from = i / (float)Around * MathF.Tau;
            float to = (i + 1) / (float)Around * MathF.Tau;

            Vector3 Foot(float angle) => At(angle, _beamReach, 0f);
            Vector3 Head(float angle) => At(angle, head, _beamHeight);

            int at = i * 6;
            sleeve[at + 0] = Foot(from);
            sleeve[at + 1] = Head(from);
            sleeve[at + 2] = Head(to);
            sleeve[at + 3] = Foot(from);
            sleeve[at + 4] = Head(to);
            sleeve[at + 5] = Foot(to);
        }

        _beamBuffer = _device.CreateBuffer<Vector3>(sleeve, BindFlags.VertexBuffer);
        _beamVertexCount = sleeve.Length;
    }

    /// <summary>
    /// Draws the beam, last of all and added to what is already there.
    /// </summary>
    /// <remarks>
    /// Last because it is haze: it has to know what is behind it to add to it.
    /// Both sides of the sleeve are drawn, and neither writes depth, so the
    /// near wall, the model and the far wall all show through one another.
    /// </remarks>
    private void DrawBeam(ID3D11DeviceContext context, Matrix4x4 viewProjection)
    {
        if (!StandProjects || !HasStand) return;
        if (_beamBuffer is null || _beamVertexCount == 0) return;
        if (_sceneVertexShader is null || _scenePixelShader is null || _sceneConstants is null) return;
        if (_addingBlend is null || _readDepth is null) return;

        context.OMSetBlendState(_addingBlend);
        context.OMSetDepthStencilState(_readDepth);
        context.RSSetState(_rasterState);

        context.IASetInputLayout(_sceneLayout);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(_sceneVertexShader);
        context.VSSetConstantBuffer(0, _sceneConstants);
        context.PSSetShader(_scenePixelShader);
        context.PSSetConstantBuffer(0, _sceneConstants);

        var constants = new SceneConstants
        {
            WorldViewProjection = Matrix4x4.Transpose(viewProjection),
            Centre = _beamFoot,
            Reach = _beamReach,
            Height = _beamHeight,
            Eye = Camera.Position,
            Time = (float)_clock.Elapsed.TotalSeconds,
            Mode = 2f,
            Style = (float)Projects,
        };

        WriteScene(context, constants);

        context.IASetVertexBuffer(0, _beamBuffer, (uint)sizeof(float) * 3);
        context.Draw((uint)_beamVertexCount, 0);

        context.OMSetBlendState(null);
        context.OMSetDepthStencilState(_depthState);
        context.RSSetState(_rasterState);
    }

    private void DrawScene(ID3D11DeviceContext context, Matrix4x4 viewProjection)
    {
        if (_sceneVertexShader is null || _scenePixelShader is null || _sceneConstants is null) return;

        context.IASetInputLayout(_sceneLayout);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        context.VSSetShader(_sceneVertexShader);
        context.VSSetConstantBuffer(0, _sceneConstants);
        context.PSSetShader(_scenePixelShader);
        context.PSSetConstantBuffer(0, _sceneConstants);

        var constants = new SceneConstants
        {
            WorldViewProjection = Matrix4x4.Transpose(viewProjection),
            LineColour = Linear(new Vector4(0.42f, 0.46f, 0.55f, 1f)),
            Background = Linear(new Vector4(0.09f, 0.10f, 0.12f, 1f)),
            Centre = _floorCentre,
            Reach = _floorReach,
            Step = _floorStep > 0f ? _floorStep : 1f,
        };

        if (_backdropBuffer is not null)
        {
            constants.Mode = 0f;
            WriteScene(context, constants);
            context.IASetVertexBuffer(0, _backdropBuffer, (uint)sizeof(float) * 3);
            context.Draw(3, 0);
        }

        // A stand is the ground when there is one, so the grid stands down.
        if (_floorBuffer is not null && _floorVertexCount > 0 && !HasStand)
        {
            constants.Mode = 1f;
            WriteScene(context, constants);
            context.IASetVertexBuffer(0, _floorBuffer, (uint)sizeof(float) * 3);
            context.Draw((uint)_floorVertexCount, 0);
        }
    }

    private void WriteScene(ID3D11DeviceContext context, SceneConstants constants)
    {
        MappedSubresource mapped = context.Map(_sceneConstants!, MapMode.WriteDiscard);
        unsafe { *(SceneConstants*)mapped.DataPointer = constants; }
        context.Unmap(_sceneConstants!, 0);
    }

    /// <summary>Points the camera at a model and backs off to fit it.</summary>
    public void FrameModel(MeshBounds bounds)
    {
        Camera.Frame(
            new Vector3(bounds.OriginX, bounds.OriginY, bounds.OriginZ),
            bounds.Radius);

        // Off the pad by a fraction of its own height, so a tall model and a
        // short one float by the same amount to the eye.
        _lift = MathF.Max(bounds.Height, 1f) * StandLift;

        // Where the light on the stand gathers, and how far it reaches: the
        // model's own footprint, with room around it.
        _standingAt = new Vector4(
            bounds.OriginX,
            bounds.OriginY,
            0f,
            Math.Max(bounds.Radius * 0.9f, 1f));

        _framed = bounds;

        BuildFloor(bounds);
    }

    /// <summary>The bounds the ground and the stand's light were built against.</summary>
    private MeshBounds? _framed;

    /// <summary>
    /// Builds the stand's light again, leaving the view where it is.
    /// </summary>
    /// <remarks>
    /// A column is not shaped like a cone and a dome is not shaped like either,
    /// so choosing a different one has to rebuild the geometry and not only
    /// shade it differently. Framing the model again would do that too, and it
    /// was what this did - but framing also puts the camera back where it
    /// starts, so picking an effect threw away whatever the viewer had orbited,
    /// panned and zoomed to.
    /// </remarks>
    public void RebuildProjection()
    {
        if (_framed is not MeshBounds bounds) return;

        BuildBeam(bounds, _floorCentre.Z);
    }

    /// <summary>
    /// Puts a stand under the model, or clears it when given nothing.
    /// </summary>
    public void SetStand(PedestalMesh? stand)
    {
        ReleaseStand();

        if (!Ready || stand is null || stand.Positions.Count == 0 || stand.Indices.Count == 0) return;

        int count = stand.Positions.Count;
        var vertices = new RenderVertex[count];

        for (int i = 0; i < count; i++)
        {
            vertices[i] = new RenderVertex
            {
                Position = stand.Positions[i],
                Normal = i < stand.Normals.Count ? stand.Normals[i] : Vector3.UnitZ,
                Uv = i < stand.TexCoords.Count ? stand.TexCoords[i] : Vector2.Zero,
                Tangent = i < stand.Tangents.Count ? stand.Tangents[i] : new Vector4(Vector3.UnitX, 1f),
            };
        }

        var indices = new uint[stand.Indices.Count];
        for (int i = 0; i < indices.Length; i++)
        {
            int index = stand.Indices[i];
            indices[i] = index >= 0 && index < count ? (uint)index : 0u;
        }

        _standVertexBuffer = _device!.CreateBuffer(vertices, BindFlags.VertexBuffer);
        _standIndexBuffer = _device.CreateBuffer(indices, BindFlags.IndexBuffer);
        _standIndexCount = indices.Length;

        if (stand.Colour is not null)
            _standColour = TryUploadImage(stand.Colour, true, _standTextures, _standSurfaces);

        if (stand.NormalMap is not null)
            _standNormal = TryUploadImage(stand.NormalMap, false, _standTextures, _standSurfaces);

        if (stand.Mask is not null)
            _standMask = TryUploadImage(stand.Mask, false, _standTextures, _standSurfaces);

        _standGloss = Selector(stand.GlossChannel);
        _standReflect = Selector(stand.ReflectChannel);

        _stand = stand;
        _standSize = stand.Size;
    }

    /// <summary>
    /// Sizes the stand to the model and puts its top face under the model's feet.
    /// </summary>
    /// <remarks>
    /// Fitted to the model rather than shown at its authored size, because the
    /// two have nothing to do with each other: this game's models are around a
    /// hundred units tall and a stand made elsewhere can be any size at all. So
    /// its width is set from the model's own footprint, and its top - not its
    /// middle or its base - is brought up to where the model's feet are.
    /// </remarks>
    /// <summary>
    /// How much of a model's width its feet actually occupy. A model is measured
    /// across its arms, and stands on a patch a fraction of that across.
    /// </summary>
    private const float FeetShareOfSpan = 0.4f;

    public void FitStand(MeshBounds bounds)
    {
        if (!HasStand || MathF.Max(_standSize.X, _standSize.Y) <= 0f) return;

        float footprint = MathF.Max(bounds.Width, bounds.Depth);
        if (footprint <= 0f) footprint = MathF.Max(bounds.Radius, 1f);

        // Comfortably wider than the model, so it reads as something stood on
        // rather than something balanced on.
        // Sized against the stand's longer side, not its width along one named
        // axis. A stand that is not square would otherwise change size when it
        // is turned, which is not something turning something should do.
        float across = MathF.Max(_standSize.X, _standSize.Y);

        float wanted = footprint * 2.2f;
        float scale = wanted / MathF.Max(across, 0.0001f);

        // The circle the model's feet stand in, in the stand's own units.
        //
        // Its feet, not its whole span: a model's footprint is measured across
        // its outstretched arms, and asking for the surface over all of that
        // catches whatever the stand does at its edges. On one platform whose
        // middle is dished, the answer over the full span came out at the rim
        // rather than the floor the model would stand in.
        float covers = (footprint * 0.5f * FeetShareOfSpan) / MathF.Max(scale, 0.0001f);
        float surface = _stand?.SurfaceWithin(covers) ?? 0f;

        // The model's own lowest point where it is known, and the bounds only
        // as a fallback.
        float feet = _modelFloor != 0f ? _modelFloor : bounds.OriginZ - bounds.ExtentZ;

        _standTransform =
            Matrix4x4.CreateScale(scale) *
            Matrix4x4.CreateTranslation(bounds.OriginX, bounds.OriginY, feet - (surface * scale));
    }

    /// <summary>Lets go of the stand, leaving the model alone.</summary>
    public void ReleaseStand()
    {
        foreach (ID3D11ShaderResourceView view in _standSurfaces) view.Dispose();
        _standSurfaces.Clear();

        foreach (ID3D11Texture2D texture in _standTextures) texture.Dispose();
        _standTextures.Clear();

        _standColour = null;
        _standNormal = null;
        _standMask = null;

        _standVertexBuffer?.Dispose();
        _standVertexBuffer = null;
        _standIndexBuffer?.Dispose();
        _standIndexBuffer = null;
        _standIndexCount = 0;

        _standTransform = Matrix4x4.Identity;
        _stand = null;
    }

    /// <summary>
    /// Draws the stand, with the same shader the model uses so it is lit by the
    /// same rules.
    /// </summary>
    private void DrawStand(ID3D11DeviceContext context, Matrix4x4 viewProjection)
    {
        if (!HasStand || _standVertexBuffer is null || _standIndexBuffer is null) return;

        var constants = new FrameConstants
        {
            WorldViewProjection = Matrix4x4.Transpose(_standTransform * viewProjection),
            World = Matrix4x4.Transpose(_standTransform),
            CameraDirection = Camera.Direction,
            BaseColour = Linear(BaseColour),
            HasTexture = _standColour is not null && ShowTextures ? 1f : 0f,
            HasSpecular = _standMask is not null && ShowTextures ? 1f : 0f,
            HasEnvironment = 0f,
            HasNormalMap = _standNormal is not null && ShowTextures ? 1f : 0f,

            // The stand's own colour tints its reflection, which is the whole
            // difference between metal that looks like something and metal that
            // looks like flat grey plastic.
            //
            // A file made this way records a metal's colour in its colour map -
            // that is what a metallic workflow means - so a metal surface should
            // reflect in its own colour. The game's costumes are the opposite
            // case: their colour map is the unlit, in-shadow colour, so they
            // carry a separate map for this and fall back to white. Reading this
            // stand the costume way replaced most of its surface with the flat
            // grey stand-in reflection: its metallic map averages 149 of 255, so
            // nearly three fifths of every pixel was that grey.
            HasSpecularColour = _standColour is not null && ShowTextures ? 1f : 0f,
            GlossSelect = _standGloss,
            ReflectSelect = _standReflect,
            SharpSelect = Vector4.Zero,
            HasRamp = 0f,
            EnvironmentLevels = 0f,

            HologramAt = _standingAt,
            Hologram = new Vector4(
                StandProjects ? 1f : 0f,
                (float)_clock.Elapsed.TotalSeconds,
                0f,
                0f),
        };

        MappedSubresource mapped = context.Map(_frameConstants!, MapMode.WriteDiscard);
        unsafe { *(FrameConstants*)mapped.DataPointer = constants; }
        context.Unmap(_frameConstants!, 0);

        context.IASetInputLayout(_inputLayout);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.IASetVertexBuffer(0, _standVertexBuffer, RenderVertex.Stride);
        context.IASetIndexBuffer(_standIndexBuffer, Format.R32_UInt, 0);

        context.VSSetShader(_vertexShader);
        context.VSSetConstantBuffer(0, _frameConstants);
        context.PSSetShader(_pixelShader);
        context.PSSetConstantBuffer(0, _frameConstants);
        context.PSSetSampler(0, Sampling);

        context.RSSetState(_rasterState);
        context.OMSetDepthStencilState(_depthState);

        context.PSSetShaderResource(0, _standColour);
        context.PSSetShaderResource(1, _standMask);
        context.PSSetShaderResource(2, null);
        context.PSSetShaderResource(3, _standNormal);
        context.PSSetShaderResource(4, _standColour);

        context.DrawIndexed((uint)_standIndexCount, 0, 0);
    }

    /// <summary>
    /// Drops the model, leaving the stand and the ground where they are.
    /// </summary>
    /// <remarks>
    /// Needed because a package holding no model used to leave the previous one
    /// on screen underneath the message saying there was none, which read as the
    /// tool showing the wrong character.
    /// </remarks>
    public void ClearModel() => ReleaseModel();

    private void ReleaseModel()
    {
        _parts.Clear();

        foreach (ID3D11ShaderResourceView view in _surfaces) view.Dispose();
        _surfaces.Clear();

        foreach (ID3D11Texture2D texture in _surfaceTextures) texture.Dispose();
        _surfaceTextures.Clear();

        _vertexBuffer?.Dispose();
        _vertexBuffer = null;
        _indexBuffer?.Dispose();
        _indexBuffer = null;
        _indexCount = 0;
    }

    /// <summary>Draws one frame and puts it on screen.</summary>
    public void Draw()
    {
        if (!Ready || _backBufferView is null) return;

        ID3D11DeviceContext context = _context!;

        context.OMSetRenderTargets(_backBufferView, _depthView);
        context.RSSetViewport(0, 0, _width, _height);
        context.ClearRenderTargetView(_backBufferView, new Color4(0.09f, 0.10f, 0.12f, 1f));

        if (_depthView is not null)
            context.ClearDepthStencilView(_depthView, DepthStencilClearFlags.Depth, 1f, 0);

        Matrix4x4 sceneViewProjection = Camera.View * Camera.Projection(_width / (float)_height);

        context.RSSetState(_rasterState);
        context.OMSetDepthStencilState(_depthState);
        DrawScene(context, sceneViewProjection);

        DrawStand(context, sceneViewProjection);

        if (_indexCount > 0)
        {
            // Only where there is something to float above. With no stand the
            // model sits on the grid, and lifting it off that would read as the
            // ground being in the wrong place.
            Matrix4x4 world = HasStand
                ? Matrix4x4.CreateTranslation(0f, 0f, _lift)
                : Matrix4x4.Identity;
            Matrix4x4 viewProjection = sceneViewProjection;

            var constants = new FrameConstants
            {
                // Row-major on this side, column-major in the shader, so the
                // matrices are transposed on the way across.
                WorldViewProjection = Matrix4x4.Transpose(world * viewProjection),
                World = Matrix4x4.Transpose(world),
                CameraDirection = Camera.Direction,
                BaseColour = Linear(BaseColour),
            };

            context.IASetInputLayout(_inputLayout);
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            context.IASetVertexBuffer(0, _vertexBuffer!, RenderVertex.Stride);
            context.IASetIndexBuffer(_indexBuffer!, Format.R32_UInt, 0);

            context.VSSetShader(_vertexShader);
            context.VSSetConstantBuffer(0, _frameConstants);
            context.PSSetShader(_pixelShader);
            context.PSSetConstantBuffer(0, _frameConstants);
            context.PSSetSampler(0, Sampling);

            context.RSSetState(_rasterState);
            context.OMSetDepthStencilState(_depthState);

            foreach (DrawPart part in _parts)
            {
                bool textured = part.Surface is not null && ShowTextures;

                constants.HasTexture = textured ? 1f : 0f;
                constants.HasSpecular = textured && part.Mask is not null ? 1f : 0f;
                constants.HasEnvironment = textured && part.Environment is not null ? 1f : 0f;
                constants.HasSpecularColour = textured && part.Tint is not null ? 1f : 0f;
                constants.HasRamp = textured && part.Ramp is not null ? 1f : 0f;
                constants.GlossSelect = part.GlossSelect;
                constants.ReflectSelect = part.ReflectSelect;
                constants.SharpSelect = part.SharpSelect;
                constants.RimSelect = part.RimSelect;
                constants.EnvironmentLevels = part.EnvironmentLevels;

                ShadingConstants.Fill(ref constants, part.Shading, textured, part.Normal is not null,
                                      _tracedFrame);

                context.RSSetState(part.TwoSided ? _rasterState : _solidState);

                MappedSubresource mapped = context.Map(_frameConstants!, MapMode.WriteDiscard);
                unsafe { *(FrameConstants*)mapped.DataPointer = constants; }
                context.Unmap(_frameConstants!, 0);

                context.PSSetShaderResource(0, part.Surface);
                context.PSSetShaderResource(1, part.Mask);
                context.PSSetShaderResource(2, part.Environment);
                context.PSSetShaderResource(3, part.Normal);
                context.PSSetShaderResource(4, part.Tint);
                context.PSSetShaderResource(5, part.Ramp);
                context.DrawIndexed((uint)part.IndexCount, (uint)part.BaseIndex, 0);
            }
        }

        DrawBeam(context, sceneViewProjection);

        _swapChain!.Present(1, PresentFlags.None);
    }

    public void Dispose()
    {
        ReleaseModel();
        ReleaseRenderTargets();

        ReleaseStand();

        _floorBuffer?.Dispose();
        _backdropBuffer?.Dispose();
        _sceneConstants?.Dispose();
        _sceneLayout?.Dispose();
        _scenePixelShader?.Dispose();
        _sceneVertexShader?.Dispose();

        _beamBuffer?.Dispose();
        _beamBuffer = null;
        _addingBlend?.Dispose();
        _addingBlend = null;
        _readDepth?.Dispose();
        _readDepth = null;
        _sharperSampler?.Dispose();
        _sharperSampler = null;
        _sampler?.Dispose();
        _solidState?.Dispose();
        _depthState?.Dispose();
        _rasterState?.Dispose();
        _frameConstants?.Dispose();
        _inputLayout?.Dispose();
        _pixelShader?.Dispose();
        _vertexShader?.Dispose();
        _swapChain?.Dispose();
        _context?.Dispose();
        _device?.Dispose();

        _device = null;
        _context = null;
        _swapChain = null;
    }
}

/// <summary>
/// The panel's native side, which is the only way to give it a swap chain.
/// </summary>
[ComImport]
[Guid("63AAD0B8-7C24-40FF-85A8-640D944CC325")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISwapChainPanelNative
{
    void SetSwapChain(IntPtr swapChain);
}
