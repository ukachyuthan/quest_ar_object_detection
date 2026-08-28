// Aspect-fit a camera frame into the detector's square input, padding with grey.
//
// This replaces the whole of YuvLetterbox.kt from the Kotlin app. There, the
// frame arrived as CPU-side YUV_420_888 and rotate + downscale + YUV->RGB +
// normalise were fused into one nearest-neighbour pass to keep up. Here the
// passthrough frame is already an RGB GPU texture, so the entire conversion is a
// single blit and the CPU never touches pixel data.
Shader "QuestObjectMemory/Letterbox"
{
    Properties
    {
        _MainTex ("Source", 2D) = "black" {}

        // srcUV = destUV * _Scale + _Offset. Set by YoloWorldDetector from the
        // source/destination aspect ratios.
        _Scale ("Scale", Vector) = (1, 1, 0, 0)
        _Offset ("Offset", Vector) = (0, 0, 0, 0)

        // Ultralytics letterboxes with (114,114,114); matching it keeps the
        // padding statistically identical to what the model saw in training.
        _PadColor ("Pad Colour", Color) = (0.447, 0.447, 0.447, 1)

        // Blit UVs are bottom-left origin, but YOLO expects row 0 at the top of
        // the image. Exposed as a toggle because the effective orientation also
        // depends on the platform's texture origin, and getting it wrong shows up
        // as vertically mirrored boxes rather than a crash.
        _FlipY ("Flip Y", Float) = 1
    }

    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _Scale;
            float4 _Offset;
            fixed4 _PadColor;
            float _FlipY;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv;
                if (_FlipY > 0.5) uv.y = 1.0 - uv.y;

                float2 src = uv * _Scale.xy + _Offset.xy;

                // Outside the fitted content: grey bar.
                if (src.x < 0.0 || src.x > 1.0 || src.y < 0.0 || src.y > 1.0)
                {
                    return _PadColor;
                }

                return tex2D(_MainTex, src);
            }
            ENDCG
        }
    }

    Fallback Off
}
