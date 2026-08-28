package com.questdetect.ar

import ai.onnxruntime.OnnxTensor
import ai.onnxruntime.OrtEnvironment
import ai.onnxruntime.OrtSession
import android.content.Context
import org.json.JSONObject
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.FloatBuffer

/**
 * Runs the prompt-baked YOLO-World ONNX graph.
 *
 * The open-vocabulary text encoder ran once at export time (model/export.py), so
 * what ships in assets is an ordinary fixed-class YOLOv8 detector whose classes
 * happen to be the phrases from model/prompts.txt.
 */
class YoloDetector(context: Context) : AutoCloseable {

    val inputSize: Int
    val labels: List<String>

    private val environment: OrtEnvironment = OrtEnvironment.getEnvironment()
    private val session: OrtSession
    private val inputName: String
    private val inputShape: LongArray

    /** Direct buffer the frame converter writes into, reused every frame. */
    val inputBuffer: FloatBuffer

    private var output = FloatArray(0)

    init {
        val meta = JSONObject(
            context.assets.open(LABELS_ASSET).bufferedReader().use { it.readText() }
        )
        inputSize = meta.getInt("imgsz")
        labels = meta.getJSONArray("classes").let { array ->
            List(array.length()) { array.getString(it) }
        }

        val options = OrtSession.SessionOptions().apply {
            setIntraOpNumThreads(THREADS)
            setOptimizationLevel(OrtSession.SessionOptions.OptLevel.ALL_OPT)
        }
        val bytes = context.assets.open(MODEL_ASSET).use { it.readBytes() }
        session = environment.createSession(bytes, options)
        inputName = session.inputNames.first()
        inputShape = longArrayOf(1, 3, inputSize.toLong(), inputSize.toLong())

        inputBuffer = ByteBuffer
            .allocateDirect(3 * inputSize * inputSize * Float.SIZE_BYTES)
            .order(ByteOrder.nativeOrder())
            .asFloatBuffer()
    }

    /** Runs on whatever [inputBuffer] currently holds. Not thread-safe by design. */
    fun detect(letterbox: Letterbox): List<Detection> {
        inputBuffer.rewind()
        OnnxTensor.createTensor(environment, inputBuffer, inputShape).use { input ->
            session.run(mapOf(inputName to input)).use { result ->
                val tensor = result[0] as OnnxTensor
                val shape = tensor.info.shape          // [1, 4 + numClasses, anchors]
                val channels = shape[1].toInt()
                val anchors = shape[2].toInt()

                if (output.size != channels * anchors) output = FloatArray(channels * anchors)
                tensor.floatBuffer.get(output)

                return decode(output, channels, anchors, letterbox)
            }
        }
    }

    private fun decode(
        raw: FloatArray,
        channels: Int,
        anchors: Int,
        letterbox: Letterbox,
    ): List<Detection> {
        val numClasses = channels - 4
        val candidates = ArrayList<Detection>()

        for (i in 0 until anchors) {
            var bestScore = 0f
            var bestClass = -1
            for (c in 0 until numClasses) {
                val score = raw[(4 + c) * anchors + i]
                if (score > bestScore) {
                    bestScore = score
                    bestClass = c
                }
            }
            if (bestClass < 0 || bestScore < CONFIDENCE_THRESHOLD) continue

            // Model space: centre-xywh in pixels of the square letterboxed input.
            val cx = raw[i]
            val cy = raw[anchors + i]
            val bw = raw[2 * anchors + i]
            val bh = raw[3 * anchors + i]

            val w = letterbox.sourceWidth.toFloat()
            val h = letterbox.sourceHeight.toFloat()
            val left = ((cx - bw / 2f - letterbox.padX) / letterbox.scale / w).coerceIn(0f, 1f)
            val top = ((cy - bh / 2f - letterbox.padY) / letterbox.scale / h).coerceIn(0f, 1f)
            val right = ((cx + bw / 2f - letterbox.padX) / letterbox.scale / w).coerceIn(0f, 1f)
            val bottom = ((cy + bh / 2f - letterbox.padY) / letterbox.scale / h).coerceIn(0f, 1f)
            if (right - left < MIN_SIZE || bottom - top < MIN_SIZE) continue

            candidates.add(
                Detection(bestClass, labels[bestClass], bestScore, left, top, right, bottom)
            )
        }

        candidates.sortByDescending { it.score }
        // Two passes: the first drops duplicate boxes of the same class, the
        // second collapses near-synonyms across classes — "tv" and "computer
        // monitor" both fire hard on the same screen, and one screen should get
        // one box carrying the higher-scoring label.
        return suppress(suppress(candidates, IOU_SAME_CLASS, sameClassOnly = true), IOU_CROSS_CLASS, sameClassOnly = false)
    }

    private fun suppress(
        sorted: List<Detection>,
        threshold: Float,
        sameClassOnly: Boolean,
    ): List<Detection> {
        val kept = ArrayList<Detection>(sorted.size)
        outer@ for (candidate in sorted) {
            for (k in kept) {
                if (sameClassOnly && k.classIndex != candidate.classIndex) continue
                if (iou(k, candidate) > threshold) continue@outer
            }
            kept.add(candidate)
            if (kept.size >= MAX_DETECTIONS) break
        }
        return kept
    }

    private fun iou(a: Detection, b: Detection): Float {
        val x1 = maxOf(a.left, b.left)
        val y1 = maxOf(a.top, b.top)
        val x2 = minOf(a.right, b.right)
        val y2 = minOf(a.bottom, b.bottom)
        val intersection = maxOf(0f, x2 - x1) * maxOf(0f, y2 - y1)
        if (intersection <= 0f) return 0f
        val union = (a.right - a.left) * (a.bottom - a.top) +
            (b.right - b.left) * (b.bottom - b.top) - intersection
        return if (union <= 0f) 0f else intersection / union
    }

    override fun close() {
        session.close()
    }

    companion object {
        const val MODEL_ASSET = "detector.onnx"
        const val LABELS_ASSET = "labels.json"

        /** YOLO-World scores are strongly bimodal — real hits land above ~0.4,
         *  noise below ~0.02 — so this sits in the empty middle. */
        const val CONFIDENCE_THRESHOLD = 0.25f
        const val IOU_SAME_CLASS = 0.45f
        const val IOU_CROSS_CLASS = 0.75f
        const val MAX_DETECTIONS = 20
        const val MIN_SIZE = 0.01f

        /** Quest 3's XR2 Gen 2 has 8 cores; leaving headroom keeps preview at 60Hz. */
        const val THREADS = 4
    }
}
