namespace QuestObjectMemory
{
    /// <summary>
    /// One detected object, in normalised camera-image space with the origin at
    /// the TOP-LEFT.
    ///
    /// Note the origin: PassthroughCameraAccess.ViewportPointToRay expects
    /// BOTTOM-LEFT normalised coordinates, so anything converting a Detection
    /// into a world ray has to flip Y. DetectionProjector is the only place that
    /// should be doing that.
    /// </summary>
    public readonly struct Detection
    {
        public readonly int ClassIndex;
        public readonly string Label;
        public readonly float Score;
        public readonly float Left;
        public readonly float Top;
        public readonly float Right;
        public readonly float Bottom;

        public Detection(int classIndex, string label, float score,
                         float left, float top, float right, float bottom)
        {
            ClassIndex = classIndex;
            Label = label;
            Score = score;
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public float CenterX => (Left + Right) * 0.5f;
        public float CenterY => (Top + Bottom) * 0.5f;
        public float Width => Right - Left;
        public float Height => Bottom - Top;
    }

    /// <summary>
    /// How a camera frame was fitted into the model's square input: the source
    /// was scaled by <see cref="Scale"/> and centred with <see cref="PadX"/> /
    /// <see cref="PadY"/> pixels of grey border. Inverting this maps model-space
    /// boxes back onto the frame.
    /// </summary>
    public readonly struct Letterbox
    {
        public readonly float Scale;
        public readonly float PadX;
        public readonly float PadY;
        public readonly int SourceWidth;
        public readonly int SourceHeight;

        public Letterbox(float scale, float padX, float padY, int sourceWidth, int sourceHeight)
        {
            Scale = scale;
            PadX = padX;
            PadY = padY;
            SourceWidth = sourceWidth;
            SourceHeight = sourceHeight;
        }

        /// <summary>
        /// Computes the aspect-fit transform placing a
        /// <paramref name="sourceWidth"/> x <paramref name="sourceHeight"/> frame
        /// centred inside a square <paramref name="inputSize"/> model input.
        /// </summary>
        public static Letterbox Fit(int sourceWidth, int sourceHeight, int inputSize)
        {
            var scale = System.Math.Min(
                inputSize / (float)sourceWidth,
                inputSize / (float)sourceHeight);
            var padX = (inputSize - sourceWidth * scale) * 0.5f;
            var padY = (inputSize - sourceHeight * scale) * 0.5f;
            return new Letterbox(scale, padX, padY, sourceWidth, sourceHeight);
        }
    }
}
