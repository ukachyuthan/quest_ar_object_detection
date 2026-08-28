package com.questdetect.ar

import android.media.Image
import java.nio.FloatBuffer
import kotlin.math.min
import kotlin.math.roundToInt

/**
 * Converts a Camera2 YUV_420_888 frame straight into the model's letterboxed
 * NCHW float input.
 *
 * This deliberately fuses rotate + downscale + colour-convert + normalise into a
 * single nearest-neighbour pass. Converting the full 1280x960 frame to RGB and
 * then resizing would touch ~8x more pixels per frame for no accuracy gain at a
 * 448px input.
 */
class YuvLetterbox(private val size: Int) {

    private val scratch = FloatArray(3 * size * size)

    /**
     * @param rotation degrees clockwise needed to make the frame upright (0/90/180/270).
     * @param dst direct float buffer sized 3*size*size, rewound on return.
     */
    fun fill(image: Image, rotation: Int, dst: FloatBuffer): Letterbox {
        val bufW = image.width
        val bufH = image.height
        val rot = ((rotation % 360) + 360) % 360
        val quarterTurn = rot == 90 || rot == 270
        val upW = if (quarterTurn) bufH else bufW
        val upH = if (quarterTurn) bufW else bufH

        val scale = min(size.toFloat() / upW, size.toFloat() / upH)
        val outW = (upW * scale).roundToInt().coerceAtMost(size)
        val outH = (upH * scale).roundToInt().coerceAtMost(size)
        val padX = (size - outW) / 2
        val padY = (size - outH) / 2

        java.util.Arrays.fill(scratch, PAD_VALUE)

        val yPlane = image.planes[0]
        val uPlane = image.planes[1]
        val vPlane = image.planes[2]
        val yBuf = yPlane.buffer
        val uBuf = uPlane.buffer
        val vBuf = vPlane.buffer
        val yRow = yPlane.rowStride
        val yPix = yPlane.pixelStride
        val uRow = uPlane.rowStride
        val uPix = uPlane.pixelStride
        val vRow = vPlane.rowStride
        val vPix = vPlane.pixelStride

        val plane = size * size
        val inv = 1f / scale

        for (oy in 0 until outH) {
            val upY = (oy * inv).toInt().coerceIn(0, upH - 1)
            var row = (oy + padY) * size + padX
            for (ox in 0 until outW) {
                val upX = (ox * inv).toInt().coerceIn(0, upW - 1)

                // Undo the display rotation to find the pixel in the raw buffer.
                val bx: Int
                val by: Int
                when (rot) {
                    90 -> { bx = upY; by = bufH - 1 - upX }
                    180 -> { bx = bufW - 1 - upX; by = bufH - 1 - upY }
                    270 -> { bx = bufW - 1 - upY; by = upX }
                    else -> { bx = upX; by = upY }
                }

                val luma = (yBuf.get(by * yRow + bx * yPix).toInt() and 0xFF)
                val cIdxU = (by shr 1) * uRow + (bx shr 1) * uPix
                val cIdxV = (by shr 1) * vRow + (bx shr 1) * vPix
                val cb = (uBuf.get(cIdxU).toInt() and 0xFF) - 128
                val cr = (vBuf.get(cIdxV).toInt() and 0xFF) - 128

                val r = luma + 1.402f * cr
                val g = luma - 0.344136f * cb - 0.714136f * cr
                val b = luma + 1.772f * cb

                scratch[row] = clamp01(r)
                scratch[plane + row] = clamp01(g)
                scratch[2 * plane + row] = clamp01(b)
                row++
            }
        }

        dst.rewind()
        dst.put(scratch)
        dst.rewind()

        return Letterbox(scale, padX.toFloat(), padY.toFloat(), upW, upH)
    }

    private fun clamp01(v: Float): Float = when {
        v <= 0f -> 0f
        v >= 255f -> 1f
        else -> v * (1f / 255f)
    }

    private companion object {
        /** Ultralytics letterboxes with grey 114; matching it keeps the padding in-distribution. */
        const val PAD_VALUE = 114f / 255f
    }
}
