using System;
using NUnit.Framework;

namespace QuestObjectMemory.Tests
{
    /// <summary>
    /// Covers the decode + two-stage NMS ported from the Kotlin app.
    ///
    /// This is the only part of the pipeline that can be proven without a
    /// headset, and it is also the part most likely to be broken by a silent
    /// change in tensor layout or letterbox maths — so it is worth pinning
    /// precisely.
    /// </summary>
    public class DetectionDecoderTests
    {
        // The project's actual classes, from model/prompts.txt.
        private static readonly string[] Labels =
        {
            "speaker", "subwoofer", "tv", "computer monitor", "pc case",
        };

        private const int Speaker = 0;
        private const int Tv = 2;
        private const int ComputerMonitor = 3;

        private const int Anchors = 16;
        private const int Channels = 4 + 5;

        // Quest 3 passthrough stream fitted into the 448px graph from export.py.
        private const int SourceWidth = 1280;
        private const int SourceHeight = 960;
        private const int InputSize = 448;

        private static Letterbox Fit() => Letterbox.Fit(SourceWidth, SourceHeight, InputSize);

        [Test]
        public void Fit_LetterboxesPillarboxFreeAndPadsVertically()
        {
            var lb = Fit();

            // 1280x960 into 448x448: width is the binding dimension, so the
            // content is 448x336 with 56px grey bars top and bottom.
            Assert.That(lb.Scale, Is.EqualTo(0.35f).Within(1e-5f));
            Assert.That(lb.PadX, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(lb.PadY, Is.EqualTo(56f).Within(1e-5f));
        }

        [Test]
        public void Decode_MapsModelSpaceBoxBackOntoTheFrame()
        {
            var tensor = NewTensor();

            // Dead centre of the model input, 10% of the content wide.
            WriteBox(tensor, anchor: 0, cx: 224f, cy: 224f, w: 44.8f, h: 33.6f);
            WriteScore(tensor, anchor: 0, classIndex: Tv, score: 0.9f);

            var results = Decode(tensor);

            Assert.That(results, Has.Count.EqualTo(1));

            var d = results[0];
            Assert.That(d.Label, Is.EqualTo("tv"));
            Assert.That(d.Left, Is.EqualTo(0.45f).Within(1e-4f));
            Assert.That(d.Right, Is.EqualTo(0.55f).Within(1e-4f));

            // The vertical padding has to be subtracted before rescaling, or the
            // box drifts up the frame.
            Assert.That(d.Top, Is.EqualTo(0.45f).Within(1e-4f));
            Assert.That(d.Bottom, Is.EqualTo(0.55f).Within(1e-4f));
        }

        [Test]
        public void Decode_DropsDetectionsBelowTheConfidenceThreshold()
        {
            var tensor = NewTensor();
            WriteBox(tensor, anchor: 0, cx: 224f, cy: 224f, w: 44.8f, h: 33.6f);
            WriteScore(tensor, anchor: 0, classIndex: Tv, score: 0.2f); // default threshold is 0.25

            Assert.That(Decode(tensor), Is.Empty);
        }

        [Test]
        public void Decode_CollapsesTvAndComputerMonitorOntoTheHigherScoringLabel()
        {
            var tensor = NewTensor();

            // One physical screen, two overlapping boxes: this is the documented
            // real-world behaviour of these two prompts, and the second NMS pass
            // exists specifically to resolve it.
            WriteBox(tensor, anchor: 0, cx: 224f, cy: 224f, w: 100f, h: 80f);
            WriteScore(tensor, anchor: 0, classIndex: Tv, score: 0.91f);

            WriteBox(tensor, anchor: 1, cx: 226f, cy: 225f, w: 100f, h: 80f);
            WriteScore(tensor, anchor: 1, classIndex: ComputerMonitor, score: 0.62f);

            var results = Decode(tensor);

            Assert.That(results, Has.Count.EqualTo(1), "overlapping screen labels should collapse to one box");
            Assert.That(results[0].Label, Is.EqualTo("tv"));
        }

        [Test]
        public void Decode_KeepsTwoSeparateSpeakers()
        {
            var tensor = NewTensor();

            WriteBox(tensor, anchor: 0, cx: 100f, cy: 100f, w: 40f, h: 40f);
            WriteScore(tensor, anchor: 0, classIndex: Speaker, score: 0.9f);

            WriteBox(tensor, anchor: 1, cx: 350f, cy: 350f, w: 40f, h: 40f);
            WriteScore(tensor, anchor: 1, classIndex: Speaker, score: 0.8f);

            var results = Decode(tensor);

            // Non-overlapping boxes of the same class are distinct objects, and a
            // stereo pair is the motivating case for this whole project.
            Assert.That(results, Has.Count.EqualTo(2));
            Assert.That(results[0].Score, Is.GreaterThan(results[1].Score), "results should be score-ordered");
        }

        [Test]
        public void Decode_DropsSubPixelBoxes()
        {
            var tensor = NewTensor();
            WriteBox(tensor, anchor: 0, cx: 224f, cy: 224f, w: 1f, h: 1f);
            WriteScore(tensor, anchor: 0, classIndex: Speaker, score: 0.99f);

            Assert.That(Decode(tensor), Is.Empty, "a 1px box is noise regardless of its score");
        }

        [Test]
        public void Decode_ClampsBoxesThatRunOffTheFrame()
        {
            var tensor = NewTensor();

            // Box centred on the left edge, so half of it is off-frame.
            WriteBox(tensor, anchor: 0, cx: 0f, cy: 224f, w: 120f, h: 80f);
            WriteScore(tensor, anchor: 0, classIndex: Speaker, score: 0.8f);

            var results = Decode(tensor);

            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0].Left, Is.EqualTo(0f));
            Assert.That(results[0].Right, Is.GreaterThan(0f));
        }

        [Test]
        public void Decode_RejectsLabelsThatDisagreeWithTheModel()
        {
            var tensor = NewTensor();

            // A stale labels.json is the likeliest failure after editing
            // prompts.txt without re-exporting, and it must not silently
            // mislabel every detection.
            Assert.Throws<ArgumentException>(() => DetectionDecoder.Decode(
                tensor, Channels, Anchors, Fit(), new[] { "speaker", "tv" }, DecoderSettings.Default));
        }

        private static System.Collections.Generic.List<Detection> Decode(float[] tensor) =>
            DetectionDecoder.Decode(tensor, Channels, Anchors, Fit(), Labels, DecoderSettings.Default);

        private static float[] NewTensor() => new float[Channels * Anchors];

        /// <summary>Writes centre-xywh, in pixels of the square model input.</summary>
        private static void WriteBox(float[] tensor, int anchor, float cx, float cy, float w, float h)
        {
            tensor[0 * Anchors + anchor] = cx;
            tensor[1 * Anchors + anchor] = cy;
            tensor[2 * Anchors + anchor] = w;
            tensor[3 * Anchors + anchor] = h;
        }

        private static void WriteScore(float[] tensor, int anchor, int classIndex, float score) =>
            tensor[(4 + classIndex) * Anchors + anchor] = score;
    }
}
