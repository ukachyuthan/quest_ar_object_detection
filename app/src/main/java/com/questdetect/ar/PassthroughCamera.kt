package com.questdetect.ar

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.graphics.ImageFormat
import android.hardware.camera2.CameraAccessException
import android.hardware.camera2.CameraCaptureSession
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraDevice
import android.hardware.camera2.CameraManager
import android.hardware.camera2.CaptureRequest
import android.hardware.camera2.params.OutputConfiguration
import android.hardware.camera2.params.SessionConfiguration
import android.media.Image
import android.media.ImageReader
import android.os.Handler
import android.os.HandlerThread
import android.util.Log
import android.util.Size
import android.view.Surface
import java.util.concurrent.Executor

/**
 * Opens a Quest 3 / 3S passthrough camera through plain Camera2.
 *
 * Horizon OS exposes the passthrough cameras as ordinary Camera2 devices,
 * distinguished only by two Meta vendor tags. Falls back to the rear camera when
 * those tags are absent, so the same APK runs on a phone for testing.
 *
 * See: developers.meta.com/horizon/documentation/spatial-sdk/spatial-sdk-pca-kotlin-api
 */
class PassthroughCamera(
    private val context: Context,
    private val callback: Callback,
) {

    interface Callback {
        /**
         * The camera and its stream size have been chosen but nothing is open
         * yet. The host must size its preview surface to [CameraInfo.size] and
         * then call [openPreview] — Camera2 derives the stream resolution from
         * the surface's buffer size, so it has to be set before the session is
         * configured.
         */
        fun onCameraResolved(info: CameraInfo)

        /** Frames are now flowing to the preview surface. */
        fun onCameraStreaming(info: CameraInfo)

        /** Delivered on the camera thread. The receiver owns the image and must close it. */
        fun onFrame(image: Image)

        fun onCameraError(message: String)
    }

    data class CameraInfo(
        val cameraId: String,
        val size: Size,
        val rotationDegrees: Int,
        val isPassthrough: Boolean,
        val description: String,
    )

    private var thread: HandlerThread? = null
    private var handler: Handler? = null
    private var device: CameraDevice? = null
    private var session: CameraCaptureSession? = null
    private var reader: ImageReader? = null
    private var info: CameraInfo? = null
    private var characteristics: CameraCharacteristics? = null

    /**
     * Picks a camera and stream size. Answers on [Callback.onCameraResolved];
     * call [openPreview] once the preview surface has been sized to match.
     *
     * @param displayRotationDegrees the activity's current display rotation (0/90/180/270).
     */
    fun resolve(displayRotationDegrees: Int) {
        if (thread != null) return
        HandlerThread("passthrough-camera").also {
            it.start()
            thread = it
            handler = Handler(it.looper)
        }
        handler!!.post { resolveOnCameraThread(displayRotationDegrees) }
    }

    /** Opens the resolved camera and starts streaming to [previewSurface]. */
    fun openPreview(previewSurface: Surface) {
        handler?.post { open(previewSurface) }
    }

    fun stop() {
        handler?.post {
            try {
                session?.close()
                device?.close()
                reader?.close()
            } catch (t: Throwable) {
                Log.w(TAG, "error during teardown", t)
            }
            session = null
            device = null
            reader = null
        }
        thread?.quitSafely()
        thread = null
        handler = null
    }

    private fun resolveOnCameraThread(displayRotationDegrees: Int) {
        val manager = context.getSystemService(CameraManager::class.java)
        if (manager == null) {
            callback.onCameraError("Camera service unavailable on this device.")
            return
        }

        val selected = try {
            select(manager)
        } catch (e: CameraAccessException) {
            callback.onCameraError("Could not enumerate cameras: ${e.message}")
            return
        }
        if (selected == null) {
            callback.onCameraError(
                "No passthrough camera found.\n\n" +
                    "Needs a Quest 3 or 3S on Horizon OS v74+ with passthrough enabled."
            )
            return
        }

        val resolvedCharacteristics = manager.getCameraCharacteristics(selected.id)
        characteristics = resolvedCharacteristics
        val size = chooseSize(resolvedCharacteristics)
        if (size == null) {
            callback.onCameraError("Camera ${selected.id} exposes no YUV_420_888 output sizes.")
            return
        }

        val sensorOrientation =
            resolvedCharacteristics.get(CameraCharacteristics.SENSOR_ORIENTATION) ?: 0
        val rotation = ((sensorOrientation - displayRotationDegrees) % 360 + 360) % 360

        val resolved = CameraInfo(
            cameraId = selected.id,
            size = size,
            rotationDegrees = rotation,
            isPassthrough = selected.source == CAMERA_SOURCE_PASSTHROUGH,
            description = buildString {
                append(if (selected.source == CAMERA_SOURCE_PASSTHROUGH) "passthrough" else "standard")
                when (selected.position) {
                    POSITION_LEFT -> append(" left")
                    POSITION_RIGHT -> append(" right")
                }
                append(" · id ").append(selected.id)
            },
        )
        info = resolved
        callback.onCameraResolved(resolved)
    }

    private fun open(previewSurface: Surface) {
        val resolved = info ?: return
        val resolvedCharacteristics = characteristics ?: return
        val manager = context.getSystemService(CameraManager::class.java) ?: return
        val size = resolved.size

        if (!hasCameraPermission()) {
            callback.onCameraError("Camera permission was revoked.")
            return
        }

        val imageReader = ImageReader.newInstance(
            size.width, size.height, ImageFormat.YUV_420_888, IMAGE_BUFFERS
        )
        imageReader.setOnImageAvailableListener({ r ->
            val image = try { r.acquireLatestImage() } catch (t: Throwable) { null }
            if (image != null) callback.onFrame(image)
        }, handler)
        reader = imageReader

        try {
            manager.openCamera(resolved.cameraId, object : CameraDevice.StateCallback() {
                override fun onOpened(camera: CameraDevice) {
                    device = camera
                    configure(camera, previewSurface, imageReader, resolved, resolvedCharacteristics)
                }

                override fun onDisconnected(camera: CameraDevice) {
                    camera.close()
                    device = null
                }

                override fun onError(camera: CameraDevice, error: Int) {
                    camera.close()
                    device = null
                    callback.onCameraError("Camera failed to open (error $error).")
                }
            }, handler)
        } catch (e: SecurityException) {
            callback.onCameraError("Camera permission denied: ${e.message}")
        } catch (e: CameraAccessException) {
            callback.onCameraError("Could not open camera ${resolved.cameraId}: ${e.message}")
        }
    }

    private fun configure(
        camera: CameraDevice,
        previewSurface: Surface,
        imageReader: ImageReader,
        resolved: CameraInfo,
        characteristics: CameraCharacteristics,
    ) {
        val targets = listOf(previewSurface, imageReader.surface)
        val executor = Executor { command -> handler?.post(command) ?: command.run() }

        val stateCallback = object : CameraCaptureSession.StateCallback() {
            override fun onConfigured(configured: CameraCaptureSession) {
                session = configured
                val request = camera.createCaptureRequest(CameraDevice.TEMPLATE_PREVIEW).apply {
                    targets.forEach { addTarget(it) }
                    // Passthrough cameras are fixed-focus; only ask for AF where
                    // the device actually advertises the mode.
                    val afModes = characteristics
                        .get(CameraCharacteristics.CONTROL_AF_AVAILABLE_MODES)
                        ?.toList() ?: emptyList()
                    if (CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_PICTURE in afModes) {
                        set(
                            CaptureRequest.CONTROL_AF_MODE,
                            CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_PICTURE
                        )
                    }
                }
                try {
                    configured.setRepeatingRequest(request.build(), null, handler)
                    callback.onCameraStreaming(resolved)
                } catch (e: CameraAccessException) {
                    callback.onCameraError("Could not start the capture stream: ${e.message}")
                }
            }

            override fun onConfigureFailed(configured: CameraCaptureSession) {
                callback.onCameraError("Camera session configuration failed.")
            }
        }

        try {
            camera.createCaptureSession(
                SessionConfiguration(
                    SessionConfiguration.SESSION_REGULAR,
                    targets.map { OutputConfiguration(it) },
                    executor,
                    stateCallback,
                )
            )
        } catch (e: CameraAccessException) {
            callback.onCameraError("Could not create the capture session: ${e.message}")
        }
    }

    private data class Candidate(
        val id: String,
        val source: Int?,
        val position: Int?,
        val facing: Int?,
    )

    private fun select(manager: CameraManager): Candidate? {
        val candidates = manager.cameraIdList.map { id ->
            val characteristics = manager.getCameraCharacteristics(id)
            Candidate(
                id = id,
                source = vendorInt(characteristics, KEY_CAMERA_SOURCE),
                position = vendorInt(characteristics, KEY_CAMERA_POSITION),
                facing = characteristics.get(CameraCharacteristics.LENS_FACING),
            )
        }
        candidates.forEach { Log.i(TAG, "camera ${it.id}: source=${it.source} position=${it.position} facing=${it.facing}") }

        val passthrough = candidates.filter { it.source == CAMERA_SOURCE_PASSTHROUGH }
        return passthrough.firstOrNull { it.position == PREFERRED_POSITION }
            ?: passthrough.firstOrNull()
            // Not a Quest: fall back to the rear camera so the app is testable on a phone.
            ?: candidates.firstOrNull { it.facing == CameraCharacteristics.LENS_FACING_BACK }
            ?: candidates.firstOrNull()
    }

    /**
     * Meta's vendor tags are not in the public key list, and the type they
     * marshal to has varied between Horizon OS builds, so try each plausible
     * representation instead of trusting one.
     */
    private fun vendorInt(characteristics: CameraCharacteristics, name: String): Int? {
        readKey(characteristics, name, Int::class.java)?.let { return it }
        readKey(characteristics, name, Integer::class.java)?.let { return it.toInt() }
        readKey(characteristics, name, Byte::class.java)?.let { return it.toInt() }
        readKey(characteristics, name, ByteArray::class.java)?.let {
            if (it.isNotEmpty()) return it[0].toInt()
        }
        return null
    }

    private fun <T> readKey(
        characteristics: CameraCharacteristics,
        name: String,
        type: Class<T>,
    ): T? = try {
        characteristics.get(CameraCharacteristics.Key(name, type))
    } catch (t: Throwable) {
        null
    }

    private fun chooseSize(characteristics: CameraCharacteristics): Size? {
        val map = characteristics.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP)
            ?: return null
        val sizes = map.getOutputSizes(ImageFormat.YUV_420_888)?.toList().orEmpty()
        if (sizes.isEmpty()) return null
        // Largest stream that still fits the budget; the Quest offers 1280x960.
        return sizes.filter { it.width.toLong() * it.height <= MAX_PIXELS }
            .maxByOrNull { it.width.toLong() * it.height }
            ?: sizes.minByOrNull { it.width.toLong() * it.height }
    }

    private fun hasCameraPermission(): Boolean =
        context.checkSelfPermission(HEADSET_CAMERA_PERMISSION) == PackageManager.PERMISSION_GRANTED ||
            context.checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED

    companion object {
        private const val TAG = "PassthroughCamera"

        const val HEADSET_CAMERA_PERMISSION = "horizonos.permission.HEADSET_CAMERA"

        // Meta vendor tags on CameraCharacteristics.
        const val KEY_CAMERA_SOURCE = "com.meta.extra_metadata.camera_source"
        const val KEY_CAMERA_POSITION = "com.meta.extra_metadata.position"

        const val CAMERA_SOURCE_PASSTHROUGH = 0
        const val POSITION_LEFT = 0
        const val POSITION_RIGHT = 1

        /** Left eye reads as the natural viewpoint for a single-camera overlay. */
        const val PREFERRED_POSITION = POSITION_LEFT

        const val MAX_PIXELS = 1_400_000L
        const val IMAGE_BUFFERS = 3
    }
}
