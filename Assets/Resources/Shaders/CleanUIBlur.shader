Shader "Custom/CleanUIBlur"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)

        [Header(Frosted Glass)]
        _BlurSize        ("Blur Radius",       Range(1.0, 12.0)) = 5.0
        _GlassTint       ("Glass Tint",        Color)            = (0.78, 0.85, 1.0, 0.18)
        _NoiseStrength   ("Frost Distortion",  Range(0.0, 0.01)) = 0.0018
        _Opacity         ("Glass Opacity",     Range(0.0, 1.0))  = 0.82
        _TopHighlight    ("Top Highlight",     Range(0.0, 1.0))  = 0.55
        _HighlightWidth  ("Highlight Width",   Range(0.02, 0.5)) = 0.12

        [Header(UI Stencil)]
        _StencilComp     ("Stencil Comparison", Float) = 8
        _Stencil         ("Stencil ID",         Float) = 0
        _StencilOp       ("Stencil Operation",  Float) = 0
        _StencilWriteMask("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask",  Float) = 255
        _ColorMask       ("Color Mask",         Float) = 15

        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue"             = "Transparent"
            "IgnoreProjector"   = "True"
            "RenderType"        = "Transparent"
            "PreviewType"       = "Plane"
            "CanUseSpriteAtlas" = "True"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target 3.0

            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP

            struct appdata_t
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex        : SV_POSITION;
                fixed4 color         : COLOR;
                float2 texcoord      : TEXCOORD0;
                float4 worldPosition : TEXCOORD1;
                float4 screenPos     : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D   _MainTex;
            fixed4      _Color;
            fixed4      _TextureSampleAdd;
            float4      _ClipRect;

            float  _BlurSize;
            fixed4 _GlassTint;
            float  _NoiseStrength;
            float  _Opacity;
            float  _TopHighlight;
            float  _HighlightWidth;

            // URP Opaque Texture — requires "Opaque Texture" enabled in the URP Renderer asset.
            UNITY_DECLARE_SCREENSPACE_TEXTURE(_CameraOpaqueTexture);
            float4 _CameraOpaqueTexture_TexelSize;

            // ────────────────────────────────────────────────
            //  Vertex shader
            // ────────────────────────────────────────────────
            v2f vert(appdata_t IN)
            {
                v2f OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.worldPosition = IN.vertex;
                OUT.vertex        = UnityObjectToClipPos(OUT.worldPosition);
                OUT.texcoord      = IN.texcoord;
                OUT.color         = IN.color * _Color;
                OUT.screenPos     = ComputeScreenPos(OUT.vertex);
                return OUT;
            }

            // ────────────────────────────────────────────────
            //  Procedural hash for frost distortion
            // ────────────────────────────────────────────────
            float2 hash22(float2 p)
            {
                float3 p3 = frac(float3(p.xyx) * float3(443.897, 441.423, 437.195));
                p3 += dot(p3, p3.yzx + 19.19);
                return frac((p3.xx + p3.yz) * p3.zy) * 2.0 - 1.0;
            }

            // ────────────────────────────────────────────────
            //  5-tap Kawase blur (mobile-optimised)
            // ────────────────────────────────────────────────
            half3 KawaseBlur(float2 uv, float2 texelSize, float radius)
            {
                float2 off = texelSize * radius * 1.5;
                half3 col  = UNITY_SAMPLE_SCREENSPACE_TEXTURE(_CameraOpaqueTexture, uv).rgb * 4.0;
                col += UNITY_SAMPLE_SCREENSPACE_TEXTURE(_CameraOpaqueTexture, uv + float2(-off.x, -off.y)).rgb;
                col += UNITY_SAMPLE_SCREENSPACE_TEXTURE(_CameraOpaqueTexture, uv + float2( off.x, -off.y)).rgb;
                col += UNITY_SAMPLE_SCREENSPACE_TEXTURE(_CameraOpaqueTexture, uv + float2(-off.x,  off.y)).rgb;
                col += UNITY_SAMPLE_SCREENSPACE_TEXTURE(_CameraOpaqueTexture, uv + float2( off.x,  off.y)).rgb;
                return col / 8.0;
            }

            // ────────────────────────────────────────────────
            //  Fragment shader
            // ────────────────────────────────────────────────
            fixed4 frag(v2f IN) : SV_Target
            {
                // ── 1. Screen UV ──
                float2 screenUV = IN.screenPos.xy / IN.screenPos.w;

                // ── 2. Frost distortion ──
                float2 noiseUV  = IN.texcoord * 80.0;
                float2 distort  = hash22(noiseUV) * _NoiseStrength;
                screenUV       += distort;

                // ── 3. Blur camera feed ──
                float2 texelSize   = _CameraOpaqueTexture_TexelSize.xy;
                half3  blurred     = KawaseBlur(screenUV, texelSize, _BlurSize);

                // ── 4. Glass tint (subtle cool lift, like frosted glass) ──
                half3 tinted = lerp(blurred, _GlassTint.rgb, _GlassTint.a);

                // Slight luminance lift so the glass feels solid, not black on dark scenes
                tinted = max(tinted, half3(0.14, 0.16, 0.20));

                // ── 5. Micro frost grain ──
                float grain  = frac(sin(dot(IN.texcoord * 1200.0, float2(12.9898, 78.233))) * 43758.5453);
                tinted      += (grain - 0.5) * 0.018;

                // ── 6. Inner top highlight arc (iOS 26 specular band) ──
                //    UV (0,1) = bottom-left in Unity UI elements; texcoord.y == 1 is the top.
                //    We want the highlight at the TOP of the element.
                float topDist     = 1.0 - IN.texcoord.y;           // 0 at top, 1 at bottom
                float highlight   = smoothstep(_HighlightWidth, 0.0, topDist);

                // Feather the highlight horizontally so it doesn't touch the edges hard
                float hFade       = smoothstep(0.0, 0.2, IN.texcoord.x)
                                  * smoothstep(1.0, 0.8, IN.texcoord.x);
                highlight        *= hFade;
                tinted           += highlight * _TopHighlight;

                // ── 7. Read UI sprite (icons / text sit on top) ──
                half4 sprite = (tex2D(_MainTex, IN.texcoord) + _TextureSampleAdd) * IN.color;

                // ── 8. Composite: glass bg + sprite on top ──
                //    Key fix: glass is ALWAYS drawn at _Opacity, regardless of sprite alpha.
                //    Sprite alpha only controls sprite blending OVER the glass.
                half3 finalRGB = lerp(tinted, sprite.rgb, sprite.a);
                float finalA   = _Opacity;   // glass shape is defined by the VisualElement geometry

                half4 output = half4(finalRGB, finalA);

                // ── 9. UI clipping ──
                #ifdef UNITY_UI_CLIP_RECT
                float2 inside = step(_ClipRect.xy, IN.worldPosition.xy)
                              * step(IN.worldPosition.xy, _ClipRect.zw);
                output.a *= inside.x * inside.y;
                #endif

                #ifdef UNITY_UI_ALPHACLIP
                clip(output.a - 0.001);
                #endif

                return output;
            }
            ENDCG
        }
    }
}
