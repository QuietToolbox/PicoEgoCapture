// EgoEncoder.java — MediaCodec H.264 + MediaMuxer，把 RGBA 帧编码成 mp4。
// 默认包，C# 侧用 new AndroidJavaObject("EgoEncoder") 访问。
import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaFormat;
import android.media.MediaMuxer;
import android.util.Log;
import java.nio.ByteBuffer;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public class EgoEncoder {
    private static final String TAG = "EgoEncoder";
    private MediaCodec codec;
    private MediaMuxer muxer;
    private int trackIndex = -1;
    private boolean muxerStarted = false;
    private int width, height;
    private byte[] nv12;
    private byte[] rgbaBytes;
    private final MediaCodec.BufferInfo bufferInfo = new MediaCodec.BufferInfo();
    private boolean running = false;
    private ExecutorService pool;
    private int nThreads;

    public boolean start(String outPath, int w, int h, int fps, int bitrate) {
        try {
            width = w; height = h;
            nv12 = new byte[w * h * 3 / 2];
            rgbaBytes = new byte[w * h * 4];
            nThreads = Math.max(2, Runtime.getRuntime().availableProcessors() - 1);
            pool = Executors.newFixedThreadPool(nThreads);
            MediaFormat fmt = MediaFormat.createVideoFormat("video/avc", w, h);
            fmt.setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatYUV420SemiPlanar);
            fmt.setInteger(MediaFormat.KEY_BIT_RATE, bitrate);
            fmt.setInteger(MediaFormat.KEY_FRAME_RATE, fps);
            fmt.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 1);
            codec = MediaCodec.createEncoderByType("video/avc");
            codec.configure(fmt, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
            codec.start();
            muxer = new MediaMuxer(outPath, MediaMuxer.OutputFormat.MUXER_OUTPUT_MPEG_4);
            running = true;
            Log.i(TAG, "started " + w + "x" + h + " @" + fps + " -> " + outPath);
            return true;
        } catch (Exception e) {
            Log.e(TAG, "start failed", e);
            return false;
        }
    }

    private long _frames, _convNs, _totNs;
    // rgba: 直接 ByteBuffer(零拷贝传入),先 bulk-get 到 byte[] 再转换
    public void encodeFrame(java.nio.ByteBuffer rgba, long ptsUs) {
        if (!running) return;
        try {
            long s = System.nanoTime();
            rgba.position(0);
            rgba.get(rgbaBytes);
            rgbaToNv12(rgbaBytes);
            long c = System.nanoTime();
            int inIdx = codec.dequeueInputBuffer(10000);
            if (inIdx >= 0) {
                ByteBuffer ib = codec.getInputBuffer(inIdx);
                ib.clear();
                ib.put(nv12);
                codec.queueInputBuffer(inIdx, 0, nv12.length, ptsUs, 0);
            }
            drain(false);
            long e = System.nanoTime();
            _convNs += (c - s); _totNs += (e - s); _frames++;
            if (_frames % 20 == 0)
                Log.i(TAG, "timing/frame: conv=" + (_convNs / _frames / 1000000) + "ms  total(java)=" + (_totNs / _frames / 1000000) + "ms  threads=" + nThreads);
        } catch (Exception e) {
            Log.e(TAG, "encodeFrame failed", e);
        }
    }

    public void stop() {
        if (!running) return;
        running = false;
        try {
            int inIdx = codec.dequeueInputBuffer(10000);
            if (inIdx >= 0) codec.queueInputBuffer(inIdx, 0, 0, 0, MediaCodec.BUFFER_FLAG_END_OF_STREAM);
            drain(true);
        } catch (Exception e) { Log.e(TAG, "stop drain failed", e); }
        try { codec.stop(); codec.release(); } catch (Exception e) {}
        try { if (muxerStarted) muxer.stop(); muxer.release(); } catch (Exception e) {}
        codec = null; muxer = null; muxerStarted = false; trackIndex = -1;
        if (pool != null) { pool.shutdown(); pool = null; }
        Log.i(TAG, "stopped");
    }

    private void drain(boolean endOfStream) {
        while (true) {
            int outIdx = codec.dequeueOutputBuffer(bufferInfo, endOfStream ? 10000 : 0);
            if (outIdx == MediaCodec.INFO_TRY_AGAIN_LATER) {
                if (!endOfStream) break; else continue;
            } else if (outIdx == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                trackIndex = muxer.addTrack(codec.getOutputFormat());
                muxer.start();
                muxerStarted = true;
            } else if (outIdx >= 0) {
                ByteBuffer ob = codec.getOutputBuffer(outIdx);
                if ((bufferInfo.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0) bufferInfo.size = 0;
                if (bufferInfo.size > 0 && muxerStarted) {
                    ob.position(bufferInfo.offset);
                    ob.limit(bufferInfo.offset + bufferInfo.size);
                    muxer.writeSampleData(trackIndex, ob, bufferInfo);
                }
                codec.releaseOutputBuffer(outIdx, false);
                if ((bufferInfo.flags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0) break;
            }
        }
    }

    // RGBA8888 -> NV12 (Y plane + interleaved UV)，BT.601 limited range —— 多核并行
    private void rgbaToNv12(byte[] rgba) {
        final int frameSize = width * height;
        final int rowsPer = (height + nThreads - 1) / nThreads;
        final CountDownLatch latch = new CountDownLatch(nThreads);
        for (int t = 0; t < nThreads; t++) {
            final int y0 = t * rowsPer;
            final int y1 = Math.min(height, y0 + rowsPer);
            pool.execute(() -> { try { if (y0 < y1) convertBand(rgba, frameSize, y0, y1); } finally { latch.countDown(); } });
        }
        try { latch.await(); } catch (InterruptedException ignored) {}
    }

    // 转换 [y0,y1) 行,绝对下标(各线程写不重叠区域 → 线程安全)
    private void convertBand(byte[] rgba, int frameSize, int y0, int y1) {
        for (int j = y0; j < y1; j++) {
            int yIndex = j * width;
            int rowBase = (height - 1 - j) * width * 4;  // 垂直翻转:相机缓冲自下而上
            boolean evenRow = (j & 1) == 0;
            int uvRow = frameSize + (j >> 1) * width;
            for (int i = 0; i < width; i++) {
                int p = rowBase + i * 4;
                int r = rgba[p] & 0xff;
                int g = rgba[p + 1] & 0xff;
                int b = rgba[p + 2] & 0xff;
                int y = (((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
                nv12[yIndex++] = (byte) (y < 0 ? 0 : (y > 255 ? 255 : y));
                if (evenRow && (i & 1) == 0) {
                    int u = (((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                    int v = (((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
                    int uv = uvRow + i; // (i/2)*2 = i (i 为偶)
                    nv12[uv] = (byte) (u < 0 ? 0 : (u > 255 ? 255 : u));
                    nv12[uv + 1] = (byte) (v < 0 ? 0 : (v > 255 ? 255 : v));
                }
            }
        }
    }
}
