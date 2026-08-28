package com.questdetect.ar

/**
 * One detected object. The rect is normalised to [0,1] in *upright* camera-image
 * space with the origin top-left, which is also the coordinate space the preview
 * is drawn in — so the overlay can scale it straight to view pixels.
 */
data class Detection(
    val classIndex: Int,
    val label: String,
    val score: Float,
    val left: Float,
    val top: Float,
    val right: Float,
    val bottom: Float,
)

/**
 * How a camera frame was fitted into the model's square input: the source was
 * scaled by [scale] and centred with [padX]/[padY] pixels of grey border.
 * Inverting this maps model-space boxes back onto the frame.
 */
data class Letterbox(
    val scale: Float,
    val padX: Float,
    val padY: Float,
    val sourceWidth: Int,
    val sourceHeight: Int,
)
