using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

/// <summary>
/// Which way a triangle has to be wound to survive back-face culling.
/// </summary>
/// <remarks>
/// The game's triangles are wound opposite to the direction their surface
/// faces, on 99.9% of thirteen million of them. Turning culling on therefore
/// needs the rule that keeps that winding, and getting it the wrong way round
/// turns every model inside out. Reasoning through clip space, screen space and
/// two handedness conventions is how that gets got wrong, so it is drawn
/// instead: two triangles, wound each way, and whichever one appears is the
/// answer.
/// </remarks>
namespace OmegaAssetStudio2.RenderChecks;

public static class Facing
{
    private const string Source = """
        struct In  { float3 Position : POSITION; };
        struct Out { float4 Position : SV_POSITION; };

        Out VertexMain(In input)
        {
            Out output;
            output.Position = float4(input.Position, 1.0);
            return output;
        }

        float4 PixelMain(Out input) : SV_TARGET { return float4(1.0, 1.0, 1.0, 1.0); }
        """;

    public static void Run()
    {
        D3D11.D3D11CreateDevice(
            null, DriverType.Hardware, DeviceCreationFlags.None,
            [FeatureLevel.Level_11_0], out ID3D11Device device, out ID3D11DeviceContext context).CheckError();

        using (device)
        using (context)
        {
            Compiler.Compile(Source, "VertexMain", "facing", "vs_4_0", out Blob vertexCode, out _);
            Compiler.Compile(Source, "PixelMain", "facing", "ps_4_0", out Blob pixelCode, out _);

            using ID3D11VertexShader vertexShader = device.CreateVertexShader(vertexCode.AsSpan());
            using ID3D11PixelShader pixelShader = device.CreatePixelShader(pixelCode.AsSpan());

            using ID3D11InputLayout layout = device.CreateInputLayout(
                [new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0)], vertexCode.AsSpan());

            // Three corners going one way round, and the same three the other.
            Vector3[] oneWay = [new(-0.9f, -0.9f, 0.5f), new(0f, 0.9f, 0.5f), new(0.9f, -0.9f, 0.5f)];
            Vector3[] otherWay = [oneWay[0], oneWay[2], oneWay[1]];

            using ID3D11Texture2D target = device.CreateTexture2D(new Texture2DDescription
            {
                Width = 16, Height = 16, MipLevels = 1, ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget,
            });

            using ID3D11RenderTargetView view = device.CreateRenderTargetView(target);

            using ID3D11Texture2D readback = device.CreateTexture2D(new Texture2DDescription
            {
                Width = 16, Height = 16, MipLevels = 1, ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
            });

            foreach (bool counterClockwiseIsFront in new[] { false, true })
            {
                using ID3D11RasterizerState state = device.CreateRasterizerState(new RasterizerDescription
                {
                    FillMode = FillMode.Solid,
                    CullMode = CullMode.Back,
                    FrontCounterClockwise = counterClockwiseIsFront,
                    DepthClipEnable = true,
                });

                foreach ((string name, Vector3[] corners) in new[] { ("first order", oneWay), ("reversed", otherWay) })
                {
                    using ID3D11Buffer buffer = device.CreateBuffer(corners, BindFlags.VertexBuffer);

                    context.OMSetRenderTargets(view);
                    context.RSSetViewport(0, 0, 16, 16);
                    context.ClearRenderTargetView(view, new Color4(0f, 0f, 0f, 1f));

                    context.IASetInputLayout(layout);
                    context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                    context.IASetVertexBuffer(0, buffer, (uint)Marshal.SizeOf<Vector3>());
                    context.VSSetShader(vertexShader);
                    context.PSSetShader(pixelShader);
                    context.RSSetState(state);
                    context.Draw(3, 0);

                    context.CopyResource(readback, target);

                    MappedSubresource mapped = context.Map(readback, 0, MapMode.Read);

                    bool drew;
                    unsafe { drew = ((byte*)mapped.DataPointer)[(8 * (int)mapped.RowPitch) + (8 * 4)] > 128; }

                    context.Unmap(readback, 0);

                    Console.WriteLine("   FrontCounterClockwise " + counterClockwiseIsFront
                                      + ", corners in " + name.PadRight(12)
                                      + (drew ? " -> drawn" : " -> culled"));
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("The corners were given in the order (-0.9,-0.9) (0,0.9) (0.9,-0.9),");
        Console.WriteLine("whose winding normal points away from the camera in this space.");
    }
}
