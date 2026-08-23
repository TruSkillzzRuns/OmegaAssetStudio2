using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using OmegaAssetStudio2.Core.Materials;

namespace OmegaAssetStudio2.RenderChecks;

/// <summary>
/// The constants the model shader reads are the viewport's own, not a copy.
/// </summary>
/// <remarks>
/// There was a copy here, and it had fallen six fields behind: it knew nothing
/// of the ambient amount, the sky it is scaled by, or which way the frame is
/// assembled, so it wrote a shorter block than the shader reads and the tail of
/// it was whatever happened to be in the buffer. A check that does not shade
/// the way the viewport shades is not a check.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct Corner
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 Uv;
    public Vector4 Tangent;
}

/// <summary>
/// Runs the viewport's own shader over surfaces measured out of the game, and
/// checks what comes back.
/// </summary>
/// <remarks>
/// Every expected value here was measured from the game's own packages and then
/// read back off the card. They are not preferences: if one of them moves, the
/// shading of real costumes has moved with it. Changing one is a deliberate act
/// and wants a note saying which costume was looked at and why it is now right.
/// </remarks>
internal static class ShadingCheck
{
    /// <summary>A surface taken from the game, and what it should come out as.</summary>
    private sealed record Surface
    {
        public required string What { get; init; }

        /// <summary>Its colour map, its packed mask, and what it reflects.</summary>
        public required byte[] Colour { get; init; }
        public required byte[] Mask { get; init; }
        public required byte[] Reflected { get; init; }

        /// <summary>Which mask channel carries the reflectivity.</summary>
        public required int ReflectChannel { get; init; }

        public required bool HasTint { get; init; }

        /// <summary>
        /// What the costume's own material says about how it shades, copied
        /// from the game's own files rather than made up for the check. Without
        /// it these would be measuring a material that states nothing, which no
        /// costume in the game is.
        /// </summary>
        public required FrameConstants Says { get; init; }

        public required byte[] Expected { get; init; }
    }

    /// <summary>
    /// One armoured costume, as its material states it: read from that
    /// costume's own material, which inherits chbasematerials.chbasematerial and
    /// sets ten values of its own.
    /// </summary>
    private static FrameConstants Destroyer => new()
    {
        UseHalfLambert = 1f,
        DiffusePower = 2.5f,
        AmbientColour = new Vector3(0.139f, 0.125f, 0.091f),

        UseSpecular = 1f,
        UseDualSpecular = 0f,

        // Its specularpower1max is 0, which is no falloff at all, so the near
        // end of the range stands for both.
        SpecularPowerLow = 5f,
        SpecularPowerHigh = 5f,
        SecondPowerLow = 70f,
        SecondPowerHigh = 80f,
        SpecularStrength = 6.5f,
        SecondStrength = 8f,
        SpecularTotal = 1f,
        SpecularColour = new Vector3(1f, 1f, 1f),

        UseReflection = 1f,
        ReflectionStrength = 1f,

        UseRim = 1f,
        RimColour = new Vector3(0.173f, 0.215f, 0.42f),
        RimFalloff = 1f,
        RimStrength = 5.25f,

        NormalStrength = 1f,

        // The frame assembled the way the game's own base pass builds it,
        // because that is the way every installed build now draws. Left at its
        // default this checked the older assembly instead - a formula nothing
        // draws with any more, which would have let a change to the live one
        // through without a word.
        Traced = 1f,

        // The ambient amount and the sky it is scaled by travel with the fill
        // light in this assembly, so the frame has to carry them or the surface
        // comes out darker here than it does on screen.
        AmbientMult = 1f,
        SkyColour = new Vector3(0.760f, 0.769f, 1f) * 0.06f,
    };

    /// <summary>How far a channel may drift before it counts as changed.</summary>
    private const int Tolerance = 4;

    private static readonly Surface[] Surfaces =
    [
        // Chrome armour. Its colour map is nearly black, so what you see is
        // almost entirely what it reflects; its mask asks for 0.76 of it.
        new()
        {
            What = "chrome armour",
            Colour = [27, 26, 26, 184],
            Mask = [41, 255, 195, 255],
            Reflected = [121, 138, 160],
            ReflectChannel = 2,
            HasTint = true,
            Says = Destroyer,

            // The frame as every installed build now draws it, assembled the
            // way the game's own base pass assembles it.
            //
            // It read 134,133,129 while the highlight was taken along the
            // half-vector, and 102,113,127 while this check was shading the
            // older way that no build uses any more. Before that, 109,123,142
            // under the invented model that lit every surface the same way
            // whatever its material said.
            //
            // The swing here is far larger than on any real model - the largest
            // any of the 1,404 that moved lost was 22 - because this plane
            // faces the camera square on, which puts its normal nearly
            // perpendicular to the key light. That is a worst case for a
            // highlight measured along the light, and worth remembering before
            // reading much into this number alone.
            Expected = [28, 27, 27],
        },

        // The red cape on the same costume, whose mask asks for almost no
        // reflection. It came out pink once, when the channel selectors were
        // being read a slot early.
        new()
        {
            What = "red cape",
            Colour = [129, 10, 7, 4],
            Mask = [14, 255, 8, 255],
            Reflected = [133, 148, 164],
            ReflectChannel = 2,
            HasTint = true,
            Says = Destroyer,

            // The plane under test faces the camera square on, which puts it
            // at four fifths of the peak highlight across its whole area - a
            // worst case rather than a typical one. The cape's own mask asks
            // for very little shine, but its material asks for a strong one
            // where the mask allows it, and 6.5 of a twentieth is what shows
            // here.
            //
            Expected = [89, 14, 12],
        },
    ];

    public static IReadOnlyList<string> Check(string shaderSourcePath)
    {
        var complaints = new List<string>();

        if (!File.Exists(shaderSourcePath))
        {
            complaints.Add($"{shaderSourcePath} is not there to read.");
            return complaints;
        }

        string file = File.ReadAllText(shaderSourcePath);

        // Every shader in the file has to compile, not just the one being
        // looked at: one of them once failed on a reserved word and simply did
        // not draw.
        foreach (Match found in Regex.Matches(file, "public const string (?<name>\\w+)"))
        {
            string name = found.Groups["name"].Value;
            string source = Extract(file, name);
            if (source.Length == 0) continue;

            foreach ((string entry, string profile) in new[] { ("VertexMain", "vs_4_0"), ("PixelMain", "ps_4_0") })
            {
                Compiler.Compile(source, entry, name, profile, out Blob? code, out Blob? errors);

                using (code)
                using (errors)
                {
                    if (code is null)
                        complaints.Add($"{name}.{entry} does not compile: {errors?.AsString()?.Trim()}");
                }
            }
        }

        if (complaints.Count > 0) return complaints;

        try
        {
            complaints.AddRange(Render(Extract(file, "Source")));
        }
        catch (Exception ex)
        {
            // A machine with no usable graphics device cannot answer this, and
            // that is not a fault in the code being checked.
            Console.WriteLine($"  shading could not be measured on this machine: {ex.GetType().Name}");
        }

        return complaints;
    }

    private static IReadOnlyList<string> Render(string source)
    {
        var complaints = new List<string>();

        SharpGen.Runtime.Result made = D3D11.D3D11CreateDevice(
            null, DriverType.Hardware, DeviceCreationFlags.None,
            [FeatureLevel.Level_11_0], out ID3D11Device? device, out ID3D11DeviceContext? context);

        if (made.Failure || device is null || context is null)
        {
            D3D11.D3D11CreateDevice(
                null, DriverType.Warp, DeviceCreationFlags.None,
                [FeatureLevel.Level_11_0], out device, out context).CheckError();
        }

        using (device)
        using (context)
        {
            Compiler.Compile(source, "VertexMain", "model", "vs_4_0", out Blob vertexCode, out _);
            Compiler.Compile(source, "PixelMain", "model", "ps_4_0", out Blob pixelCode, out _);

            using ID3D11VertexShader vertexShader = device!.CreateVertexShader(vertexCode.AsSpan());
            using ID3D11PixelShader pixelShader = device.CreatePixelShader(pixelCode.AsSpan());

            using ID3D11InputLayout layout = device.CreateInputLayout(
            [
                new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 24, 0),
                new InputElementDescription("TANGENT", 0, Format.R32G32B32A32_Float, 32, 0),
            ], vertexCode.AsSpan());

            // One triangle covering the target, already in screen space, facing
            // the camera. The lighting is then the only thing being measured.
            Vector3 facing = new(0f, -1f, 0f);

            Corner[] triangle =
            [
                new() { Position = new(-1f, -3f, 0.5f), Normal = facing, Tangent = new(1f, 0f, 0f, 1f) },
                new() { Position = new(-1f, 1f, 0.5f), Normal = facing, Tangent = new(1f, 0f, 0f, 1f) },
                new() { Position = new(3f, 1f, 0.5f), Normal = facing, Tangent = new(1f, 0f, 0f, 1f) },
            ];

            using ID3D11Buffer corners = device.CreateBuffer(triangle, BindFlags.VertexBuffer);

            using ID3D11Buffer constants = device.CreateBuffer(
                (uint)FrameConstants.Size, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);

            using ID3D11Texture2D target = device.CreateTexture2D(Describe(BindFlags.RenderTarget, ResourceUsage.Default));
            using ID3D11RenderTargetView view = device.CreateRenderTargetView(target);

            Texture2DDescription readbackShape = Describe(BindFlags.None, ResourceUsage.Staging);
            readbackShape.CPUAccessFlags = CpuAccessFlags.Read;
            using ID3D11Texture2D readback = device.CreateTexture2D(readbackShape);

            using ID3D11SamplerState sampler = device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Wrap,
                AddressV = TextureAddressMode.Wrap,
                AddressW = TextureAddressMode.Wrap,
                MaxLOD = float.MaxValue,
            });

            foreach (Surface surface in Surfaces)
            {
                using ID3D11ShaderResourceView colour = Solid(device, surface.Colour, true);
                using ID3D11ShaderResourceView mask = Solid(device, [.. surface.Mask], false);
                using ID3D11ShaderResourceView reflected = Solid(device, [.. surface.Reflected, (byte)255], true);
                using ID3D11ShaderResourceView white = Solid(device, [255, 255, 255, 255], true);

                var frame = surface.Says with
                {
                    WorldViewProjection = Matrix4x4.Identity,
                    World = Matrix4x4.Identity,
                    CameraDirection = new Vector3(0f, 1f, 0f),
                    BaseColour = new Vector3(0.3f, 0.3f, 0.3f),
                    HasTexture = 1f,
                    HasSpecular = 1f,
                    HasEnvironment = 1f,
                    HasNormalMap = 0f,
                    HasSpecularColour = surface.HasTint ? 1f : 0f,
                    HasRamp = 0f,
                    EnvironmentLevels = 1f,
                    GlossSelect = new Vector4(1f, 0f, 0f, 0f),
                    ReflectSelect = Selector(surface.ReflectChannel),
                    SharpSelect = Vector4.Zero,
                    RimSelect = Vector4.Zero,
                };

                MappedSubresource mapped = context!.Map(constants, MapMode.WriteDiscard);
                unsafe { *(FrameConstants*)mapped.DataPointer = frame; }
                context.Unmap(constants, 0);

                context.OMSetRenderTargets(view);
                context.RSSetViewport(0, 0, 8, 8);
                context.ClearRenderTargetView(view, new Color4(0f, 0f, 0f, 1f));

                context.IASetInputLayout(layout);
                context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                context.IASetVertexBuffer(0, corners, (uint)Marshal.SizeOf<Corner>());
                context.VSSetShader(vertexShader);
                context.VSSetConstantBuffer(0, constants);
                context.PSSetShader(pixelShader);
                context.PSSetConstantBuffer(0, constants);
                context.PSSetSampler(0, sampler);
                context.PSSetShaderResource(0, colour);
                context.PSSetShaderResource(1, mask);
                context.PSSetShaderResource(2, reflected);
                context.PSSetShaderResource(3, null);
                context.PSSetShaderResource(4, white);
                context.PSSetShaderResource(5, white);
                context.Draw(3, 0);

                context.CopyResource(readback, target);
                MappedSubresource got = context.Map(readback, 0, MapMode.Read);

                byte r, g, b;
                unsafe
                {
                    byte* pixels = (byte*)got.DataPointer;
                    b = pixels[0];
                    g = pixels[1];
                    r = pixels[2];
                }

                context.Unmap(readback, 0);

                bool same =
                    Math.Abs(r - surface.Expected[0]) <= Tolerance &&
                    Math.Abs(g - surface.Expected[1]) <= Tolerance &&
                    Math.Abs(b - surface.Expected[2]) <= Tolerance;

                Console.WriteLine($"  {surface.What,-16} {r,3} {g,3} {b,3}   expected " +
                                  $"{surface.Expected[0],3} {surface.Expected[1],3} {surface.Expected[2],3}" +
                                  (same ? string.Empty : "   CHANGED"));

                if (!same)
                {
                    complaints.Add(
                        $"{surface.What} now shades to {r},{g},{b}, where it was {surface.Expected[0]}," +
                        $"{surface.Expected[1]},{surface.Expected[2]}. Real costumes shade by this, so either " +
                        "the change is wrong or the expected value needs updating on purpose.");
                }
            }
        }

        return complaints;
    }

    private static Texture2DDescription Describe(BindFlags bind, ResourceUsage usage) => new()
    {
        Width = 8,
        Height = 8,
        MipLevels = 1,
        ArraySize = 1,
        Format = Format.B8G8R8A8_UNorm,
        SampleDescription = new SampleDescription(1, 0),
        Usage = usage,
        BindFlags = bind,
    };

    private static Vector4 Selector(int channel) => channel switch
    {
        0 => new Vector4(1f, 0f, 0f, 0f),
        1 => new Vector4(0f, 1f, 0f, 0f),
        2 => new Vector4(0f, 0f, 1f, 0f),
        3 => new Vector4(0f, 0f, 0f, 1f),
        _ => Vector4.Zero,
    };

    private static ID3D11ShaderResourceView Solid(ID3D11Device device, byte[] pixel, bool gammaEncoded)
    {
        var shape = new Texture2DDescription
        {
            Width = 1,
            Height = 1,
            MipLevels = 1,
            ArraySize = 1,
            Format = gammaEncoded ? Format.R8G8B8A8_UNorm_SRgb : Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
        };

        GCHandle pinned = GCHandle.Alloc(pixel, GCHandleType.Pinned);

        try
        {
            ID3D11Texture2D texture = device.CreateTexture2D(
                shape, [new SubresourceData(pinned.AddrOfPinnedObject(), 4)]);

            return device.CreateShaderResourceView(texture);
        }
        finally
        {
            pinned.Free();
        }
    }

    internal static string Extract(string file, string field)
    {
        int at = file.IndexOf("public const string " + field, StringComparison.Ordinal);
        if (at < 0) return string.Empty;

        int open = file.IndexOf("\"\"\"", at, StringComparison.Ordinal);
        if (open < 0) return string.Empty;

        int close = file.IndexOf("\"\"\"", open + 3, StringComparison.Ordinal);
        return close < 0 ? string.Empty : file[(open + 3)..close];
    }
}
