package com.questdetect.ar

import android.Manifest
import android.app.Activity
import android.content.pm.PackageManager
import android.graphics.Matrix
import android.graphics.SurfaceTexture
import android.media.Image
import android.os.Bundle
import android.os.SystemClock
import android.util.Log
import android.view.Surface
import android.view.TextureView
import android.view.View
import android.view.WindowManager
import android.widget.TextView
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Live object detection over the Quest passthrough camera.
 *
 * Preview frames go straight from Camera2 to the TextureView (no CPU cost), and
 * a parallel YUV stream is sampled for inference on a background thread. Frames
 * that arrive while the detector is busy are dropped rather than queued, so the
 * boxes always describe the most recent view of the room instead of lagging
 * further behind the longer the app runs.
 */
class MainActivity : Activity(), PassthroughCamera.Callback {

    private lateinit var frame: AspectFrameLayout
    private lateinit var preview: TextureView
    private lateinit var overlay: OverlayView
    private lateinit var status: TextView

    private var camera: PassthroughCamera? = null
    private var detector: YoloDetector? = null
    private var converter: YuvLetterbox? = null
    private var cameraInfo: PassthroughCamera.CameraInfo? = null
    private var previewSurface: Surface? = null

    private val inference = Executors.newSingleThreadExecutor()
    private val busy = AtomicBoolean(false)
    private var started = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        setContentView(R.layout.activity_main)

        frame = findViewById(R.id.frame)
        preview = findViewById(R.id.preview)
        overlay = findViewById(R.id.overlay)
        status = findViewById(R.id.status)

        showStatus("Loading detector…")
        loadDetector()

        preview.surfaceTextureListener = object : TextureView.SurfaceTextureListener {
            override fun onSurfaceTextureAvailable(surface: SurfaceTexture, width: Int, height: Int) =
                maybeStartCamera()

            override fun onSurfaceTextureSizeChanged(surface: SurfaceTexture, width: Int, height: Int) =
                applyPreviewTransform()

            override fun onSurfaceTextureDestroyed(surface: SurfaceTexture): Boolean = true
            override fun onSurfaceTextureUpdated(surface: SurfaceTexture) = Unit
        }
    }

    override fun onStart() {
        super.onStart()
        if (hasCameraPermission()) maybeStartCamera() else requestCameraPermission()
    }

    override fun onStop() {
        super.onStop()
        camera?.stop()
        camera = null
        previewSurface?.release()
        previewSurface = null
        started = false
        overlay.clear()
    }

    override fun onDestroy() {
        super.onDestroy()
        inference.shutdown()
        detector?.close()
        detector = null
    }

    // region permissions

    private fun hasCameraPermission(): Boolean =
        checkSelfPermission(PassthroughCamera.HEADSET_CAMERA_PERMISSION) == PackageManager.PERMISSION_GRANTED ||
            checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED

    private fun requestCameraPermission() {
        // HEADSET_CAMERA is the Horizon OS permission and is simply reported as
        // denied on a phone, where CAMERA covers us instead.
        requestPermissions(
            arrayOf(PassthroughCamera.HEADSET_CAMERA_PERMISSION, Manifest.permission.CAMERA),
            REQUEST_CAMERA,
        )
    }

    override fun onRequestPermissionsResult(
        requestCode: Int,
        permissions: Array<out String>,
        grantResults: IntArray,
    ) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode != REQUEST_CAMERA) return
        if (hasCameraPermission()) {
            maybeStartCamera()
        } else {
            showStatus(
                "Camera permission denied.\n\n" +
                    "Grant it in Settings › Apps › Quest Object Detect › Permissions, then reopen."
            )
        }
    }

    // endregion

    private fun loadDetector() {
        inference.execute {
            try {
                val loaded = YoloDetector(this)
                val letterbox = YuvLetterbox(loaded.inputSize)
                runOnUiThread {
                    detector = loaded
                    converter = letterbox
                    overlay.setStatusLine(loaded.labels.joinToString(" · "))
                    if (started) hideStatus()
                }
                Log.i(TAG, "detector ready: ${loaded.labels} @ ${loaded.inputSize}px")
            } catch (t: Throwable) {
                Log.e(TAG, "failed to load detector", t)
                runOnUiThread {
                    showStatus(
                        "Could not load the detector.\n\n${t.message}\n\n" +
                            "Run model/export.py to regenerate app/src/main/assets/detector.onnx."
                    )
                }
            }
        }
    }

    private fun maybeStartCamera() {
        if (started || !preview.isAvailable || !hasCameraPermission()) return
        started = true
        showStatus("Opening passthrough camera…")
        PassthroughCamera(this, this).also {
            camera = it
            it.resolve(displayRotationDegrees())
        }
    }

    private fun displayRotationDegrees(): Int = when (display?.rotation) {
        Surface.ROTATION_90 -> 90
        Surface.ROTATION_180 -> 180
        Surface.ROTATION_270 -> 270
        else -> 0
    }

    // region camera callbacks

    override fun onCameraResolved(info: PassthroughCamera.CameraInfo) {
        cameraInfo = info
        runOnUiThread {
            val quarterTurn = info.rotationDegrees == 90 || info.rotationDegrees == 270
            val displayWidth = if (quarterTurn) info.size.height else info.size.width
            val displayHeight = if (quarterTurn) info.size.width else info.size.height

            val texture = preview.surfaceTexture
            if (texture == null) {
                showStatus("Preview surface went away before the camera opened.")
                return@runOnUiThread
            }
            // Must happen before the Surface reaches Camera2: the stream
            // resolution is derived from the buffer size, not requested directly.
            texture.setDefaultBufferSize(info.size.width, info.size.height)
            frame.aspectRatio = displayWidth.toFloat() / displayHeight
            // The aspect change relayouts the TextureView, so re-derive the
            // transform once the new bounds are in.
            frame.post { applyPreviewTransform() }

            if (!info.isPassthrough) {
                overlay.setStatusLine("fallback camera (${info.description})")
            }
            Log.i(TAG, "camera resolved: ${info.description} ${info.size} rot=${info.rotationDegrees}")
            previewSurface?.release()
            val surface = Surface(texture)
            previewSurface = surface
            camera?.openPreview(surface)
        }
    }

    override fun onCameraStreaming(info: PassthroughCamera.CameraInfo) {
        runOnUiThread {
            if (detector != null) hideStatus() else showStatus("Loading detector…")
        }
    }

    override fun onFrame(image: Image) {
        val model = detector
        val letterbox = converter
        if (model == null || letterbox == null || !busy.compareAndSet(false, true)) {
            image.close()
            return
        }
        val rotation = cameraInfo?.rotationDegrees ?: 0

        inference.execute {
            var closed = false
            try {
                val transform = letterbox.fill(image, rotation, model.inputBuffer)
                // Hand the buffer back to the camera before the slow part.
                image.close()
                closed = true

                val startedAt = SystemClock.elapsedRealtime()
                val results = model.detect(transform)
                val elapsed = SystemClock.elapsedRealtime() - startedAt

                runOnUiThread { overlay.update(results, elapsed) }
            } catch (t: Throwable) {
                Log.e(TAG, "inference failed", t)
            } finally {
                if (!closed) {
                    try { image.close() } catch (t: Throwable) { Log.w(TAG, "close failed", t) }
                }
                busy.set(false)
            }
        }
    }

    override fun onCameraError(message: String) {
        Log.e(TAG, message)
        runOnUiThread { showStatus(message) }
    }

    // endregion

    /**
     * TextureView stretches the camera buffer to the view bounds before applying
     * this transform, so each case maps that already-stretched image to the
     * rotated one. Derived directly rather than fitted, so it is exact for any
     * sensor orientation.
     */
    private fun applyPreviewTransform() {
        val rotation = cameraInfo?.rotationDegrees ?: 0
        val w = preview.width.toFloat()
        val h = preview.height.toFloat()
        if (w <= 0f || h <= 0f) return

        val matrix = Matrix()
        when (rotation) {
            90 -> matrix.setValues(floatArrayOf(0f, -w / h, w, h / w, 0f, 0f, 0f, 0f, 1f))
            180 -> matrix.setValues(floatArrayOf(-1f, 0f, w, 0f, -1f, h, 0f, 0f, 1f))
            270 -> matrix.setValues(floatArrayOf(0f, w / h, 0f, -h / w, 0f, h, 0f, 0f, 1f))
            else -> matrix.reset()
        }
        preview.setTransform(matrix)
    }

    private fun showStatus(message: String) {
        status.text = message
        status.visibility = View.VISIBLE
    }

    private fun hideStatus() {
        status.visibility = View.GONE
    }

    private companion object {
        const val TAG = "QuestObjectDetect"
        const val REQUEST_CAMERA = 1001
    }
}
