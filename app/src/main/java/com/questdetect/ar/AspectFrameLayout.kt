package com.questdetect.ar

import android.content.Context
import android.util.AttributeSet
import android.widget.FrameLayout
import kotlin.math.roundToInt

/**
 * Letterboxes its children to the camera's aspect ratio.
 *
 * With the preview and the overlay sharing exactly these bounds, a normalised
 * detection maps to view pixels by a plain multiply — no per-view fitting maths,
 * and no chance of the boxes drifting off the image.
 */
class AspectFrameLayout @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
    defStyleAttr: Int = 0,
) : FrameLayout(context, attrs, defStyleAttr) {

    /** width / height; non-positive means "fill the parent". */
    var aspectRatio: Float = 0f
        set(value) {
            if (field != value) {
                field = value
                requestLayout()
            }
        }

    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val availableWidth = MeasureSpec.getSize(widthMeasureSpec)
        val availableHeight = MeasureSpec.getSize(heightMeasureSpec)

        if (aspectRatio <= 0f || availableWidth == 0 || availableHeight == 0) {
            super.onMeasure(widthMeasureSpec, heightMeasureSpec)
            return
        }

        var width = availableWidth
        var height = (width / aspectRatio).roundToInt()
        if (height > availableHeight) {
            height = availableHeight
            width = (height * aspectRatio).roundToInt()
        }

        super.onMeasure(
            MeasureSpec.makeMeasureSpec(width, MeasureSpec.EXACTLY),
            MeasureSpec.makeMeasureSpec(height, MeasureSpec.EXACTLY),
        )
    }
}
