package com.questdetect.ar

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.RectF
import android.os.SystemClock
import android.util.AttributeSet
import android.view.View

/** Draws detection boxes and a small stats readout over the camera preview. */
class OverlayView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
    defStyleAttr: Int = 0,
) : View(context, attrs, defStyleAttr) {

    private var detections: List<Detection> = emptyList()
    private var inferenceMs: Long = 0
    private var framesPerSecond: Float = 0f
    private var lastUpdate: Long = 0
    private var statusLine: String = ""

    private val boxPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeWidth = 5f
        strokeCap = Paint.Cap.ROUND
    }
    private val chipPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.FILL }
    private val labelPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.BLACK
        textSize = 34f
        isFakeBoldText = true
    }
    private val hudPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.WHITE
        textSize = 32f
        isFakeBoldText = true
    }
    private val hudBackground = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.argb(150, 0, 0, 0)
        style = Paint.Style.FILL
    }

    private val rect = RectF()
    private val chip = RectF()

    fun update(results: List<Detection>, inferenceMillis: Long) {
        val now = SystemClock.elapsedRealtime()
        if (lastUpdate != 0L) {
            val instant = 1000f / (now - lastUpdate).coerceAtLeast(1L)
            // Light smoothing; the raw per-frame rate is too jumpy to read.
            framesPerSecond = if (framesPerSecond == 0f) instant else framesPerSecond * 0.8f + instant * 0.2f
        }
        lastUpdate = now
        detections = results
        inferenceMs = inferenceMillis
        invalidate()
    }

    fun setStatusLine(text: String) {
        statusLine = text
        invalidate()
    }

    fun clear() {
        detections = emptyList()
        framesPerSecond = 0f
        lastUpdate = 0
        invalidate()
    }

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        val w = width.toFloat()
        val h = height.toFloat()

        for (detection in detections) {
            val color = colorFor(detection.classIndex)
            rect.set(
                detection.left * w,
                detection.top * h,
                detection.right * w,
                detection.bottom * h,
            )

            boxPaint.color = color
            canvas.drawRoundRect(rect, 10f, 10f, boxPaint)

            val text = "${detection.label}  ${(detection.score * 100).toInt()}%"
            val textWidth = labelPaint.measureText(text)
            val chipHeight = 46f
            // Flip the chip inside the box when the object is at the top edge.
            val chipTop = if (rect.top - chipHeight >= 0f) rect.top - chipHeight else rect.top
            chip.set(rect.left, chipTop, rect.left + textWidth + 24f, chipTop + chipHeight)
            chipPaint.color = color
            canvas.drawRoundRect(chip, 8f, 8f, chipPaint)
            canvas.drawText(text, chip.left + 12f, chip.bottom - 14f, labelPaint)
        }

        drawHud(canvas)
    }

    private fun drawHud(canvas: Canvas) {
        val lines = buildList {
            add("${detections.size} object${if (detections.size == 1) "" else "s"}")
            if (inferenceMs > 0) add("${inferenceMs} ms · ${"%.1f".format(framesPerSecond)} fps")
            if (statusLine.isNotEmpty()) add(statusLine)
        }
        if (lines.isEmpty()) return

        val padding = 16f
        val lineHeight = 40f
        val boxWidth = lines.maxOf { hudPaint.measureText(it) } + padding * 2
        canvas.drawRoundRect(
            padding, padding,
            padding + boxWidth, padding + lineHeight * lines.size + padding,
            12f, 12f, hudBackground,
        )
        lines.forEachIndexed { index, line ->
            canvas.drawText(line, padding * 2, padding * 2 + lineHeight * (index + 1) - 12f, hudPaint)
        }
    }

    private fun colorFor(index: Int): Int = PALETTE[index % PALETTE.size]

    private companion object {
        val PALETTE = intArrayOf(
            0xFF4ADE80.toInt(), // speaker      — green
            0xFFF472B6.toInt(), // subwoofer    — pink
            0xFF60A5FA.toInt(), // tv           — blue
            0xFF38BDF8.toInt(), // monitor      — cyan
            0xFFFBBF24.toInt(), // pc case      — amber
            0xFFA78BFA.toInt(),
            0xFFFB7185.toInt(),
        )
    }
}
