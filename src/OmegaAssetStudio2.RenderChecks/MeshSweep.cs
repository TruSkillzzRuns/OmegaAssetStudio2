using System.Numerics;
using System.Runtime.InteropServices;
using OmegaAssetStudio2.Core.Materials;
using OmegaAssetStudio2.Core.Meshes;
using OmegaAssetStudio2.Core.Packages;
using OmegaAssetStudio2.Core.Textures;
using OmegaAssetStudio2.Core.Workspace;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace OmegaAssetStudio2.RenderChecks;

/// <summary>
/// Draws every model the mesh panel lists and grades what came out.
/// </summary>
/// <remarks>
/// Fixing shading one costume at a time, from one screenshot at a time, has
/// repeatedly put right what was in the picture and broken something not in it.
/// The only way to know a change is an improvement is to look at all of them,
/// and there are more than two thousand, so they are looked at by machine.
/// <para>
/// Nothing here judges whether a costume is the right colour - no measurement
/// can know that. It finds the states that are wrong whatever the costume is: a
/// model that came out white, one that came out black, one drawn in the flat
/// grey that means no texture reached it, and one speckled with pixels unlike
/// both their neighbours, which is what two surfaces fighting over the same
/// depth looks like.
/// </para>
/// </remarks>
public static class MeshSweep
{
    /// <summary>How big each model is drawn. Small, because there are thousands.</summary>
    private static int Side = 192;

    [StructLayout(LayoutKind.Sequential)]
    private struct Corner
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 Uv;
        public Vector4 Tangent;
    }

    /// <summary>What one model came out as.</summary>
    public sealed record Verdict
    {
        public required string Character { get; init; }
        public required string Variant { get; init; }
        public required string Package { get; init; }
        public required string Mesh { get; init; }

        public required int Slots { get; init; }
        public required int Painted { get; init; }

        /// <summary>How much of the picture the model covers.</summary>
        public required float Coverage { get; init; }

        /// <summary>Of what it covers, how much is at the top of the range.</summary>
        public required float Blown { get; init; }

        /// <summary>Of what it covers, how much is at the bottom.</summary>
        public required float Black { get; init; }

        /// <summary>
        /// Of what it covers, how much differs sharply from the pixels on both
        /// sides of it. Smooth shading does not do this; two surfaces at the
        /// same depth do.
        /// </summary>
        public required float Speckled { get; init; }

        public required IReadOnlyList<string> Faults { get; init; }

        /// <summary>
        /// How many hard edges each rendering had, in the order they were
        /// asked for. The first is the shading as it stands.
        /// </summary>
        public IReadOnlyList<int> Edges { get; init; } = [];

        /// <summary>
        /// The average brightness of what each rendering covered. Whether a
        /// change revealed detail or destroyed it is not something the count of
        /// hard edges can tell apart - both go up - but a shadow being lifted
        /// raises this and a surface being broken up does not.
        /// </summary>
        public IReadOnlyList<int> Brightness { get; init; } = [];

        public override string ToString() =>
            string.Join('\t',
                Character, Variant, Package, Mesh,
                Painted + "/" + Slots,
                Coverage.ToString("0.000"),
                Blown.ToString("0.000"),
                Black.ToString("0.000"),
                Speckled.ToString("0.000"),
                string.Join(",", Edges),
                string.Join(",", Brightness),
                Faults.Count == 0 ? "ok" : string.Join("; ", Faults));
    }

    /// <summary>How big to draw, and how close to stand. Raised to look at a fault.</summary>
    public static void Frame(int side, float closeness)
    {
        Side = Math.Clamp(side, 32, 2048);
        Closeness = Math.Clamp(closeness, 0.05f, 1f);
    }

    /// <summary>How much of the model to fill the picture with. One is all of it.</summary>
    private static float Closeness = 1f;

    /// <summary>Where the camera stands, around and above, in radians.</summary>
    public static float Around { get; set; } = 0.7853982f;
    public static float Above { get; set; } = 0.7853982f;

    /// <summary>Where to write a picture of each model drawn, when asked.</summary>
    public static string? PictureFolder { get; set; }

    /// <summary>Only draw models whose name contains this, when set.</summary>
    public static string? Only { get; set; }

    /// <summary>
    /// Terms to leave out of the shading, by name, so a fault can be traced to
    /// the one that draws it.
    /// </summary>
    /// <remarks>
    /// Switching a term off and looking is the only way to tell which of them
    /// is responsible for a mark on a face. Reasoning about which one it
    /// probably is has repeatedly picked the wrong one.
    /// </remarks>
    public static HashSet<string> Without { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The renderings to make of each model. An empty entry is the shading as
    /// it stands; every other is that shading with the named terms silenced.
    /// </summary>
    /// <remarks>
    /// Rendering the same model several ways and comparing is how a fault gets
    /// attributed to a term across the whole roster rather than argued about on
    /// one costume. The comparison used is the count of hard edges - abrupt
    /// steps between neighbouring pixels - because that is what the marks being
    /// complained about are, and smooth shading does not make them.
    /// </remarks>
    public static IReadOnlyList<string> Variants { get; set; } = [string.Empty];

    public static IReadOnlyList<Verdict> Run(string cooked, string shaderSource, int limit = int.MaxValue)
    {
        var client = new GameClient { CookedPath = cooked, RootPath = cooked, DisplayName = "sweep" };
        var locator = new ObjectLocator(PackageIndex.Build(client));
        var reader = new TextureReader(cooked);

        var verdicts = new List<Verdict>();

        D3D11.D3D11CreateDevice(
            null, DriverType.Hardware, DeviceCreationFlags.None,
            [FeatureLevel.Level_11_0], out ID3D11Device device, out ID3D11DeviceContext context).CheckError();

        using (device)
        using (context)
        {
            Compiler.Compile(shaderSource, "VertexMain", "model", "vs_4_0", out Blob vertexCode, out Blob vertexErrors);
            Compiler.Compile(shaderSource, "PixelMain", "model", "ps_4_0", out Blob pixelCode, out Blob pixelErrors);

            if (vertexCode is null || pixelCode is null)
            {
                Console.Error.WriteLine("the viewport's shader does not compile: "
                                        + (vertexErrors?.AsString() ?? pixelErrors?.AsString()));
                return verdicts;
            }

            using ID3D11VertexShader vertexShader = device.CreateVertexShader(vertexCode.AsSpan());
            using ID3D11PixelShader pixelShader = device.CreatePixelShader(pixelCode.AsSpan());

            using ID3D11InputLayout layout = device.CreateInputLayout(
            [
                new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 24, 0),
                new InputElementDescription("TANGENT", 0, Format.R32G32B32A32_Float, 32, 0),
            ], vertexCode.AsSpan());

            using ID3D11Buffer constants = device.CreateBuffer(
                (uint)FrameConstants.Size, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);

            using ID3D11SamplerState sampler = device.CreateSamplerState(new SamplerDescription
            {
                // The same sampling the viewport uses, so the grading is of
                // what the user sees. Eight is what the game's own top quality
                // bucket asks for in DefaultSystemSettings.ini.
                Filter = Filter.Anisotropic,
                MaxAnisotropy = 8,
                AddressU = TextureAddressMode.Wrap,
                AddressV = TextureAddressMode.Wrap,
                AddressW = TextureAddressMode.Wrap,
                MipLODBias = 0f,
                MaxLOD = float.MaxValue,
            });

            using ID3D11RasterizerState solid = device.CreateRasterizerState(new RasterizerDescription
            {
                FillMode = FillMode.Solid,
                CullMode = CullMode.Back,
                FrontCounterClockwise = false,
                DepthClipEnable = true,
            });

            using ID3D11RasterizerState both = device.CreateRasterizerState(new RasterizerDescription
            {
                FillMode = FillMode.Solid,
                CullMode = CullMode.None,
                DepthClipEnable = true,
            });

            using ID3D11DepthStencilState depthState = device.CreateDepthStencilState(new DepthStencilDescription
            {
                DepthEnable = true,
                DepthWriteMask = DepthWriteMask.All,
                DepthFunc = ComparisonFunction.Less,
            });

            using ID3D11Texture2D target = device.CreateTexture2D(Describe(BindFlags.RenderTarget, ResourceUsage.Default));
            using ID3D11RenderTargetView view = device.CreateRenderTargetView(target);

            using ID3D11Texture2D depth = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)Side, Height = (uint)Side, MipLevels = 1, ArraySize = 1,
                Format = Format.D32_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.DepthStencil,
            });

            using ID3D11DepthStencilView depthView = device.CreateDepthStencilView(depth);

            Texture2DDescription readbackShape = Describe(BindFlags.None, ResourceUsage.Staging);
            readbackShape.CPUAccessFlags = CpuAccessFlags.Read;
            using ID3D11Texture2D readback = device.CreateTexture2D(readbackShape);

            int done = 0;

            foreach (RosterEntry entry in CharacterRoster.Build(client))
            {
                if (done >= limit) break;

                Package package;
                try { package = Package.Open(entry.PackagePath); } catch { continue; }

                foreach (int export in package.FindExportsOfClass("SkeletalMesh"))
                {
                    if (done >= limit) break;

                    SkeletalMesh mesh;
                    try { mesh = SkeletalMeshReader.TryRead(package, export); } catch { continue; }
                    if (mesh is null || mesh.Lods.Count == 0 || !mesh.Lods[0].HasGeometry) continue;

                    if (Only is not null
                        && !mesh.Name.Contains(Only, StringComparison.OrdinalIgnoreCase)) continue;

                    done++;

                    Verdict verdict = Draw(
                        device, context, entry, package, mesh, reader, locator, cooked,
                        layout, vertexShader, pixelShader, constants, sampler,
                        solid, both, depthState, view, depthView, target, readback);

                    verdicts.Add(verdict);

                    if (done % 100 == 0) Console.WriteLine("   " + done + " drawn");
                }
            }
        }

        return verdicts;
    }

    private static Texture2DDescription Describe(BindFlags bind, ResourceUsage usage) => new()
    {
        Width = (uint)Side,
        Height = (uint)Side,
        MipLevels = 1,
        ArraySize = 1,
        Format = Format.R8G8B8A8_UNorm,
        SampleDescription = new SampleDescription(1, 0),
        Usage = usage,
        BindFlags = bind,
    };

    private static Verdict Draw(
        ID3D11Device device, ID3D11DeviceContext context,
        RosterEntry entry, Package package, SkeletalMesh mesh,
        TextureReader reader, ObjectLocator locator, string cooked,
        ID3D11InputLayout layout, ID3D11VertexShader vertexShader, ID3D11PixelShader pixelShader,
        ID3D11Buffer constants, ID3D11SamplerState sampler,
        ID3D11RasterizerState solid, ID3D11RasterizerState both,
        ID3D11DepthStencilState depthState,
        ID3D11RenderTargetView view, ID3D11DepthStencilView depthView,
        ID3D11Texture2D target, ID3D11Texture2D readback)
    {
        var faults = new List<string>();

        IReadOnlyList<MeshSurface> surfaces;
        try
        {
            // With the game folder, a material that binds no texture but was
            // compiled with a colour is painted that colour rather than left
            // bare. Reading the shader cache once costs a pause on the first
            // model and nothing after it.
            surfaces = MeshSurfaceResolver.Resolve(package, mesh, reader, null, locator, cooked);
        }
        catch (Exception ex)
        {
            surfaces = [];
            faults.Add("resolving threw " + ex.GetType().Name);
        }

        SkeletalMeshLod lod = mesh.Lods[0];

        var owned = new List<IDisposable>();

        try
        {
            var corners = new Corner[lod.Positions.Count];

            for (int i = 0; i < corners.Length; i++)
            {
                corners[i] = new Corner
                {
                    Position = lod.Positions[i],
                    Normal = i < lod.Normals.Count ? lod.Normals[i] : Vector3.UnitZ,
                    Uv = i < lod.TexCoords.Count ? lod.TexCoords[i] : Vector2.Zero,
                    Tangent = i < lod.Tangents.Count ? lod.Tangents[i] : new Vector4(1f, 0f, 0f, 1f),
                };
            }

            var indices = new uint[lod.Indices.Count];
            for (int i = 0; i < indices.Length; i++) indices[i] = (uint)lod.Indices[i];

            if (corners.Length == 0 || indices.Length == 0)
            {
                return Grade(entry, package, mesh, surfaces, faults, [], 0);
            }

            ID3D11Buffer vertices = device.CreateBuffer(corners, BindFlags.VertexBuffer);
            ID3D11Buffer triangles = device.CreateBuffer(indices, BindFlags.IndexBuffer);
            owned.Add(vertices);
            owned.Add(triangles);

            // The same model with the direction of its surface worked out from
            // its own triangles instead of read from the file. Drawing both and
            // comparing says whether a mark is in the model or in the reading
            // of it.
            var rebuilt = new Corner[corners.Length];
            Array.Copy(corners, rebuilt, corners.Length);

            var gathered = new Vector3[corners.Length];

            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                int a = (int)indices[t], b = (int)indices[t + 1], c = (int)indices[t + 2];
                if (a >= corners.Length || b >= corners.Length || c >= corners.Length) continue;

                Vector3 face = Vector3.Cross(
                    corners[b].Position - corners[a].Position,
                    corners[c].Position - corners[a].Position);

                if (face.LengthSquared() < 1e-12f) continue;

                gathered[a] += face;
                gathered[b] += face;
                gathered[c] += face;
            }

            for (int i = 0; i < rebuilt.Length; i++)
            {
                if (gathered[i].LengthSquared() < 1e-12f) continue;

                Vector3 made = Vector3.Normalize(gathered[i]);

                // Turned to point the same way the file's own normal does, so
                // only the shape of the surface is being compared and not the
                // winding convention.
                if (Vector3.Dot(made, corners[i].Normal) < 0f) made = -made;

                rebuilt[i].Normal = made;
            }

            ID3D11Buffer fromTriangles = device.CreateBuffer(rebuilt, BindFlags.VertexBuffer);
            owned.Add(fromTriangles);

            // Everything each slot needs, uploaded once.
            // How the frame is assembled is the build's answer, not a slot's,
            // and the viewport asks it the same way from the same list. Asking
            // it per surface instead left every slot whose material could not
            // be resolved shaded the older way here and the newer way on
            // screen, which is a sweep guarding a renderer that does not ship.
            bool tracedFrame = TracedFrame.Draws(GameClientLocator.BuildBesideCooked(cooked));

            var colourBySlot = new Dictionary<int, ID3D11ShaderResourceView>();
            var maskBySlot = new Dictionary<int, ID3D11ShaderResourceView>();
            var normalBySlot = new Dictionary<int, ID3D11ShaderResourceView>();
            var tintBySlot = new Dictionary<int, ID3D11ShaderResourceView>();
            var rampBySlot = new Dictionary<int, ID3D11ShaderResourceView>();
            var environmentBySlot = new Dictionary<int, ID3D11ShaderResourceView>();
            var levelsBySlot = new Dictionary<int, int>();
            var shadingBySlot = new Dictionary<int, SurfaceShading>();
            var glossBySlot = new Dictionary<int, Vector4>();
            var reflectBySlot = new Dictionary<int, Vector4>();
            var sharpBySlot = new Dictionary<int, Vector4>();
            var rimBySlot = new Dictionary<int, Vector4>();

            foreach (MeshSurface surface in surfaces)
            {
                shadingBySlot[surface.MaterialIndex] = SurfaceShadingReader.Read(surface.Settings, surface.UsesSpecularColour, surface.Given);

                Put(device, owned, colourBySlot, surface.MaterialIndex, surface.Image, surface.IsSrgb);
                Put(device, owned, normalBySlot, surface.MaterialIndex, surface.NormalMap, false);
                Put(device, owned, tintBySlot, surface.MaterialIndex, surface.Specular, true);
                Put(device, owned, rampBySlot, surface.MaterialIndex, surface.Ramp, true);

                if (surface.Mask is not null)
                {
                    Put(device, owned, maskBySlot, surface.MaterialIndex, surface.Mask, false);
                    glossBySlot[surface.MaterialIndex] = Selector(surface.GlossChannel);
                    reflectBySlot[surface.MaterialIndex] = Selector(surface.ReflectChannel);
                    sharpBySlot[surface.MaterialIndex] = Selector(surface.SharpnessChannel);
                    rimBySlot[surface.MaterialIndex] = Selector(surface.RimChannel);
                }

                if (surface.Environment is not null)
                {
                    IReadOnlyList<TextureImage> built = EnvironmentPrefilter.Build(surface.Environment);
                    ID3D11ShaderResourceView? made = Levels(device, owned, built, true);

                    if (made is not null)
                    {
                        environmentBySlot[surface.MaterialIndex] = made;
                        levelsBySlot[surface.MaterialIndex] = built.Count;
                    }
                }
            }

            // Framed the way the panel frames a model, from where it puts the
            // camera.
            Vector3 low = lod.Positions[0], high = lod.Positions[0];

            foreach (Vector3 position in lod.Positions)
            {
                low = Vector3.Min(low, position);
                high = Vector3.Max(high, position);
            }

            Vector3 centre = (low + high) * 0.5f;
            float radius = MathF.Max(0.001f, (high - low).Length() * 0.5f);

            float distance = radius * 2.6f * Closeness;
            var eye = centre + new Vector3(
                MathF.Cos(Around) * MathF.Cos(Above) * distance,
                MathF.Sin(Around) * MathF.Cos(Above) * distance,
                MathF.Sin(Above) * distance);

            // Standing close means looking at the head, since that is where
            // the faults worth looking at have been.
            if (Closeness < 0.9f)
            {
                centre = new Vector3(centre.X, centre.Y, low.Z + ((high.Z - low.Z) * 0.92f));
                eye = centre + new Vector3(
                    MathF.Cos(Around) * MathF.Cos(Above) * distance,
                    MathF.Sin(Around) * MathF.Cos(Above) * distance,
                    MathF.Sin(Above) * distance);
            }

            Matrix4x4 look = Matrix4x4.CreateLookAt(eye, centre, Vector3.UnitZ);

            Matrix4x4 lens = Matrix4x4.CreatePerspectiveFieldOfView(
                MathF.PI / 4f, 1f, MathF.Max(0.01f, distance * 0.002f), MathF.Max(1000f, radius * 40f));

            var frame = new FrameConstants
            {
                WorldViewProjection = Matrix4x4.Transpose(look * lens),
                World = Matrix4x4.Identity,
                CameraDirection = Vector3.Normalize(centre - eye),
                BaseColour = new Vector3(0.3f, 0.3f, 0.3f),
            };

            var drawn = new List<byte[]>(Variants.Count);
            int painted = 0;

            foreach (string variant in Variants)
            {
            var silenced = new HashSet<string>(
                variant.Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

            foreach (string term in Without) silenced.Add(term);

            painted = 0;

            context.OMSetRenderTargets(view, depthView);
            context.RSSetViewport(0, 0, Side, Side);
            // Cleared to nothing at all, alpha included. The shader always
            // writes an alpha of one, so a pixel's alpha says whether the model
            // was drawn there and its colour does not - which matters because
            // several costumes are black. One is a black diffuse map over black, and counting covered pixels by
            // colour called a correctly drawn costume "hardly anything was
            // drawn" while hiding whatever else was wrong with it.
            context.ClearRenderTargetView(view, new Color4(0f, 0f, 0f, 0f));
            context.ClearDepthStencilView(depthView, DepthStencilClearFlags.Depth, 1f, 0);

            context.IASetInputLayout(layout);
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            context.IASetVertexBuffer(
                0,
                silenced.Contains("storednormals") ? fromTriangles : vertices,
                (uint)Marshal.SizeOf<Corner>());
            context.IASetIndexBuffer(triangles, Format.R32_UInt, 0);
            context.VSSetShader(vertexShader);
            context.VSSetConstantBuffer(0, constants);
            context.PSSetShader(pixelShader);
            context.PSSetConstantBuffer(0, constants);
            context.PSSetSampler(0, sampler);
            context.OMSetDepthStencilState(depthState);

            foreach (MeshSection section in lod.Sections)
            {
                if (section.IndexCount <= 0) continue;

                int slot = section.MaterialIndex;

                ID3D11ShaderResourceView? colour = colourBySlot.GetValueOrDefault(slot);
                SurfaceShading shading = shadingBySlot.GetValueOrDefault(slot, SurfaceShading.Plain);

                if (colour is not null) painted++;

                FrameConstants own = frame with
                {
                    HasTexture = colour is not null ? 1f : 0f,
                    HasSpecular = colour is not null && maskBySlot.ContainsKey(slot) ? 1f : 0f,
                    HasEnvironment = colour is not null && environmentBySlot.ContainsKey(slot) ? 1f : 0f,
                    HasSpecularColour = colour is not null && tintBySlot.ContainsKey(slot) ? 1f : 0f,
                    HasRamp = colour is not null && rampBySlot.ContainsKey(slot) ? 1f : 0f,
                    GlossSelect = glossBySlot.GetValueOrDefault(slot),
                    ReflectSelect = reflectBySlot.GetValueOrDefault(slot),
                    SharpSelect = sharpBySlot.GetValueOrDefault(slot),
                    RimSelect = rimBySlot.GetValueOrDefault(slot),
                    EnvironmentLevels = levelsBySlot.GetValueOrDefault(slot),
                };

                ShadingConstants.Fill(ref own, shading, colour is not null, normalBySlot.ContainsKey(slot),
                                      tracedFrame);

                if (silenced.Count > 0) LeaveOut(ref own, silenced);

                MappedSubresource mapped = context.Map(constants, MapMode.WriteDiscard);
                unsafe { *(FrameConstants*)mapped.DataPointer = own; }
                context.Unmap(constants, 0);

                context.RSSetState(shading.TwoSided ? both : solid);

                context.PSSetShaderResource(0, colour);
                context.PSSetShaderResource(1, maskBySlot.GetValueOrDefault(slot));
                context.PSSetShaderResource(2, environmentBySlot.GetValueOrDefault(slot));
                context.PSSetShaderResource(3, normalBySlot.GetValueOrDefault(slot));
                context.PSSetShaderResource(4, tintBySlot.GetValueOrDefault(slot));
                context.PSSetShaderResource(5, rampBySlot.GetValueOrDefault(slot));

                int start = Math.Clamp(section.BaseIndex, 0, indices.Length);
                int length = Math.Clamp(section.IndexCount, 0, indices.Length - start);
                if (length <= 0) continue;

                context.DrawIndexed((uint)length, (uint)start, 0);
            }

            context.CopyResource(readback, target);

            MappedSubresource read = context.Map(readback, 0, MapMode.Read);

            byte[] pixels = new byte[Side * Side * 4];

            unsafe
            {
                byte* from = (byte*)read.DataPointer;
                for (int y = 0; y < Side; y++)
                {
                    Marshal.Copy((IntPtr)(from + (y * (int)read.RowPitch)), pixels, y * Side * 4, Side * 4);
                }
            }

            context.Unmap(readback, 0);

            if (PictureFolder is not null)
            {
                System.IO.Directory.CreateDirectory(PictureFolder);

                string what = variant.Length == 0 ? string.Empty : " without " + variant.Replace(',', ' ');
                WritePicture(System.IO.Path.Combine(PictureFolder, mesh.Name + what + ".png"), pixels);
            }

            drawn.Add(pixels);
            }

            return Grade(entry, package, mesh, surfaces, faults, drawn, painted);
        }
        finally
        {
            foreach (IDisposable held in owned) held.Dispose();
        }
    }

    /// <summary>
    /// The drawn frame, written as a PNG by hand - one uncompressed block per
    /// row, so nothing outside the runtime is needed to look at it.
    /// </summary>
    private static void WritePicture(string path, byte[] pixels)
    {
        using var file = System.IO.File.Create(path);
        using var writer = new System.IO.BinaryWriter(file);

        writer.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A });

        var head = new List<byte>();
        head.AddRange(Big(Side));
        head.AddRange(Big(Side));
        head.AddRange(new byte[] { 8, 6, 0, 0, 0 });
        Block(writer, "IHDR", head.ToArray());

        var raw = new List<byte>(pixels.Length + Side);

        for (int y = 0; y < Side; y++)
        {
            raw.Add(0);
            raw.AddRange(new ArraySegment<byte>(pixels, y * Side * 4, Side * 4));
        }

        Block(writer, "IDAT", Wrapped(raw.ToArray()));
        Block(writer, "IEND", []);
    }

    private static byte[] Wrapped(byte[] data)
    {
        var outp = new List<byte> { 0x78, 0x01 };

        int at = 0;
        while (at < data.Length)
        {
            int run = Math.Min(65535, data.Length - at);
            bool last = at + run >= data.Length;

            outp.Add((byte)(last ? 1 : 0));
            outp.Add((byte)(run & 0xFF));
            outp.Add((byte)(run >> 8));
            outp.Add((byte)(~run & 0xFF));
            outp.Add((byte)((~run >> 8) & 0xFF));

            for (int i = 0; i < run; i++) outp.Add(data[at + i]);
            at += run;
        }

        uint a = 1, b = 0;
        foreach (byte value in data) { a = (a + value) % 65521; b = (b + a) % 65521; }

        outp.AddRange(Big((int)((b << 16) | a)));
        return outp.ToArray();
    }

    private static void Block(System.IO.BinaryWriter writer, string kind, byte[] body)
    {
        writer.Write(Big(body.Length));

        var whole = new byte[4 + body.Length];
        for (int i = 0; i < 4; i++) whole[i] = (byte)kind[i];
        Array.Copy(body, 0, whole, 4, body.Length);

        writer.Write(whole);

        uint crc = 0xFFFFFFFF;
        foreach (byte value in whole)
        {
            crc ^= value;
            for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }

        writer.Write(Big((int)(crc ^ 0xFFFFFFFF)));
    }

    private static byte[] Big(int value) =>
        [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    /// <summary>Silences whichever terms were named.</summary>
    private static void LeaveOut(ref FrameConstants constants, HashSet<string> Without)
    {
        if (Without.Contains("spec")) constants.UseSpecular = 0f;
        if (Without.Contains("rim")) constants.UseRim = 0f;
        if (Without.Contains("ambient")) constants.AmbientColour = Vector3.Zero;
        if (Without.Contains("normal")) constants.HasNormalMap = 0f;
        if (Without.Contains("ramp")) constants.HasRamp = 0f;
        if (Without.Contains("reflection")) constants.UseReflection = 0f;
        if (Without.Contains("fill")) constants.UseFill = 0f;
        if (Without.Contains("cutout")) constants.Cutout = 0f;
        if (Without.Contains("mask")) constants.HasSpecular = 0f;

        // No colour map at all, so the model is drawn as bare geometry. This is
        // the one that separates a mark painted into a picture, or sampled from
        // the wrong part of one, from a mark the geometry itself makes.
        if (Without.Contains("colour")) constants.HasTexture = 0f;

        // Not a silencing but a second way of reading the falloff curve, kept
        // here so the two can be compared over the whole roster in one run.
        if (Without.Contains("wrapped")) constants.WrapLight = 1f;
        if (Without.Contains("clamped")) constants.ClampCurve = 1f;

        // The step that scales a highlight by the surface's own colour and by
        // the material's diffusespecmult, which on one costume is 55 where 551
        // of 579 materials use 2.55.
        if (Without.Contains("specdiffuse")) constants.SpecularDesaturate = 1f;

        // The highlight's colour, left white.
        if (Without.Contains("spectint")) constants.SpecularColour = Vector3.One;

        // Everything but the colour map, which shows whether a mark is painted
        // into the picture or put there by the shading.
        if (Without.Contains("everything"))
        {
            constants.UseSpecular = 0f;
            constants.UseRim = 0f;
            constants.UseReflection = 0f;
            constants.UseFill = 0f;
            constants.HasNormalMap = 0f;
            constants.HasRamp = 0f;
            constants.AmbientColour = Vector3.Zero;
        }
    }

    private static void Put(
        ID3D11Device device, List<IDisposable> owned,
        Dictionary<int, ID3D11ShaderResourceView> into, int slot, TextureImage? image, bool gamma)
    {
        if (image is null || image.Width <= 0 || image.Height <= 0) return;

        ID3D11ShaderResourceView? made = Levels(device, owned, [image], gamma);
        if (made is not null) into[slot] = made;
    }

    private static ID3D11ShaderResourceView? Levels(
        ID3D11Device device, List<IDisposable> owned, IReadOnlyList<TextureImage> levels, bool gamma)
    {
        if (levels.Count == 0) return null;

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

                ID3D11Texture2D made = device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)levels[0].Width,
                    Height = (uint)levels[0].Height,
                    MipLevels = (uint)levels.Count,
                    ArraySize = 1,
                    Format = gamma ? Format.R8G8B8A8_UNorm_SRgb : Format.R8G8B8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource,
                }, boxes);

                ID3D11ShaderResourceView seen = device.CreateShaderResourceView(made);

                owned.Add(made);
                owned.Add(seen);

                return seen;
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

    private static Vector4 Selector(int channel) => channel switch
    {
        0 => new Vector4(1f, 0f, 0f, 0f),
        1 => new Vector4(0f, 1f, 0f, 0f),
        2 => new Vector4(0f, 0f, 1f, 0f),
        3 => new Vector4(0f, 0f, 0f, 1f),
        _ => Vector4.Zero,
    };

    /// <summary>
    /// How many abrupt steps there are between neighbouring pixels.
    /// </summary>
    /// <remarks>
    /// A surface lit smoothly changes gradually across itself. A mark with a
    /// straight or stepped edge - a patch on a face, a slab across a cape -
    /// makes a jump, and counting the jumps counts the marks. Comparing this
    /// count between a rendering and the same rendering with one term silenced
    /// says how much of the marking that term is responsible for.
    /// </remarks>
    private static int HardEdges(byte[] pixels)
    {
        int found = 0;

        for (int y = 1; y < Side - 1; y++)
        {
            for (int x = 1; x < Side - 1; x++)
            {
                int at = ((y * Side) + x) * 4;

                if (pixels[at + 3] == 0) continue;

                int here = pixels[at] + pixels[at + 1] + pixels[at + 2];

                int left = pixels[at - 4] + pixels[at - 3] + pixels[at - 2];
                int up = pixels[at - (Side * 4)] + pixels[at - (Side * 4) + 1] + pixels[at - (Side * 4) + 2];

                if (pixels[at - 1] > 0 && Math.Abs(here - left) > 70) found++;
                else if (pixels[at - (Side * 4) + 3] > 0 && Math.Abs(here - up) > 70) found++;
            }
        }

        return found;
    }

    /// <summary>The average brightness of the pixels the model covers.</summary>
    private static int Brightness(byte[] pixels)
    {
        long total = 0;
        int covered = 0;

        for (int at = 0; at + 3 < pixels.Length; at += 4)
        {
            if (pixels[at + 3] == 0) continue;

            int sum = pixels[at] + pixels[at + 1] + pixels[at + 2];

            total += sum / 3;
            covered++;
        }

        return covered > 0 ? (int)(total / covered) : 0;
    }

    /// <summary>What the drawn picture says about the model.</summary>
    private static Verdict Grade(
        RosterEntry entry, Package package, SkeletalMesh mesh,
        IReadOnlyList<MeshSurface> surfaces, List<string> faults,
        IReadOnlyList<byte[]> drawn, int painted)
    {
        float coverage = 0f, blown = 0f, black = 0f, speckled = 0f;

        byte[]? pixels = drawn.Count > 0 ? drawn[0] : null;

        var edges = new List<int>(drawn.Count);
        foreach (byte[] one in drawn) edges.Add(HardEdges(one));

        var brightness = new List<int>(drawn.Count);
        foreach (byte[] one in drawn) brightness.Add(Brightness(one));

        if (pixels is not null)
        {
            int covered = 0, tooBright = 0, tooDark = 0, odd = 0;

            for (int y = 0; y < Side; y++)
            {
                for (int x = 0; x < Side; x++)
                {
                    int at = ((y * Side) + x) * 4;

                    if (pixels[at + 3] == 0) continue;

                    int r = pixels[at], g = pixels[at + 1], b = pixels[at + 2];

                    covered++;

                    if (r > 250 && g > 250 && b > 250) tooBright++;
                    if (r < 10 && g < 10 && b < 10) tooDark++;

                    // A pixel unlike both of its neighbours. Shading changes
                    // gradually across a surface; two surfaces arguing over the
                    // same depth do not.
                    if (x > 0 && x < Side - 1)
                    {
                        int before = pixels[at - 4] + pixels[at - 3] + pixels[at - 2];
                        int after = pixels[at + 4] + pixels[at + 5] + pixels[at + 6];
                        int here = r + g + b;

                        if (pixels[at - 1] > 0 && pixels[at + 7] > 0
                            && Math.Abs(here - before) > 90 && Math.Abs(here - after) > 90
                            && Math.Sign(here - before) == Math.Sign(here - after))
                        {
                            odd++;
                        }
                    }
                }
            }

            coverage = covered / (float)(Side * Side);

            if (covered > 0)
            {
                blown = tooBright / (float)covered;
                black = tooDark / (float)covered;
                speckled = odd / (float)covered;
            }
        }

        int slots = mesh.Materials.Count;

        if (painted < slots) faults.Add((slots - painted) + " of " + slots + " parts have no picture");
        if (coverage < 0.01f) faults.Add("hardly anything was drawn");
        if (blown > 0.25f) faults.Add("a quarter of it or more is pure white");
        if (black > 0.5f) faults.Add("half of it or more is pure black");

        // Speckle is recorded but not called a fault. Measured across the
        // roster it runs smoothly from nothing to a third, with no gap between
        // the models that look wrong and the ones that do not: fine detail in a
        // normal map produces it just as two surfaces at one depth do. Calling
        // everything above a line a fault flagged 1,509 of 2,107 models,
        // including plainly correct ones, which is a metric that cannot be
        // acted on. Whether a model is built twice over is answerable exactly
        // from its triangles instead, and that is what culling now handles.
        if (speckled > 0.22f) faults.Add("very speckled - worth looking at");

        return new Verdict
        {
            Character = entry.Character,
            Variant = entry.Variant,
            Package = System.IO.Path.GetFileNameWithoutExtension(entry.PackagePath),
            Mesh = mesh.Name,
            Slots = slots,
            Painted = painted,
            Coverage = coverage,
            Blown = blown,
            Black = black,
            Speckled = speckled,
            Edges = edges,
            Brightness = brightness,
            Faults = faults,
        };
    }
}
