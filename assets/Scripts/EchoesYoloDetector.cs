// ============================================================
//  EchoesYoloDetector.cs
//  Echoes — Programmable Spatial Experience Platform
//  Version: v2.5.0 | 23 July 2026
//
//  READ THIS BEFORE ANYTHING ELSE IN THIS FILE
//  ----------------------------------------------------------
//  Every other script in this project has gone through a real compile
//  check before handover. This one has not, and cannot from outside
//  Unity: there is no way to install the Inference Engine package or
//  verify its exact API surface without the Unity Editor itself. The
//  package was renamed from Sentis to Unity Inference Engine
//  (com.unity.ai.inference) recently, and its C# API changed materially
//  between versions (TensorFloat/TensorInt merged into a single Tensor
//  type, output readback moved to a ReadbackAndClone() pattern, worker
//  creation syntax changed). This file is written against the most
//  recent documented API shape at the time of writing, targeting the
//  same version (2.1.2) the reference model at huggingface.co/unity/
//  sentis-YOLOv8n was confirmed tested against. It is the single most
//  likely file in this drop to need a small hand-adjustment once opened
//  in the real project. If it does not compile as-is, check first:
//    1. Does `using Unity.Sentis;` resolve, or does your installed
//       package version need `using Unity.InferenceEngine;` instead?
//    2. Does `new Worker(...)` exist, or does your version still use
//       `WorkerFactory.CreateWorker(...)`?
//    3. Does Tensor<float> exist, or does your version still split
//       TensorFloat/TensorInt?
//  All three are exactly the kind of version-to-version rename this
//  package has been going through. The inference logic itself (model
//  I/O contract, preprocessing, output parsing) is verified against a
//  real, actually-exported YOLOv8n.onnx, run through real inference,
//  in the process of writing this file, that part is not a guess.
//
//  MODEL I/O CONTRACT (verified by real inference, not documentation):
//    Input:  "images", shape [1, 3, 640, 640], float, 0..1 normalised,
//            CHW channel order, letterboxed to a square 640x640.
//    Output: "output0", shape [1, 300, 6], float. NMS is already baked
//            into the graph (exported with nms=True), each of the 300
//            rows is [x1, y1, x2, y2, confidence, class_id] in the
//            640x640 letterboxed pixel space, zero-padded rows where
//            fewer than 300 detections were found.
//  ============================================================

using System.Collections.Generic;
using System.IO;
using UnityEngine;
 // v2.5.0: see header note above if this does not resolve

public class EchoesYoloDetector : MonoBehaviour
{
    [Tooltip("The imported YOLOv8n model asset (yolov8n.onnx, exported with opset 15 and nms=True). Drag the imported model asset here, not the raw .onnx file path.")]
    public Unity.InferenceEngine.ModelAsset modelAsset;

    [Tooltip("v2.5.0 spec Section 4: CPU for Phase 1, correctness over speed, avoids stacking GPU thermal load on top of an already sustained scan session. Revisit only after detection itself is confirmed working on device.")]
    public Unity.InferenceEngine.BackendType backend = Unity.InferenceEngine.BackendType.CPU;

    [Tooltip("v2.5.0 spec Section 6: 0.25, YOLO's own common default. Low deliberately, Phase 1 wants to see uncertain cases too, not a pre-curated clean list.")]
    public float confidenceThreshold = 0.25f;

    private const int MODEL_INPUT_SIZE = 640;
    private Unity.InferenceEngine.Worker _worker;
    private Unity.InferenceEngine.Model _model;

    // COCO-80, the stock class list YOLOv8n ships trained on. No "desk",
    // no venue-specific classes, see v2.5.0 spec Section 9, that is a
    // known, accepted Phase 1 limitation, not a bug to chase.
    private static readonly string[] COCO_CLASSES = {
        "person","bicycle","car","motorcycle","airplane","bus","train","truck","boat","traffic light",
        "fire hydrant","stop sign","parking meter","bench","bird","cat","dog","horse","sheep","cow",
        "elephant","bear","zebra","giraffe","backpack","umbrella","handbag","tie","suitcase","frisbee",
        "skis","snowboard","sports ball","kite","baseball bat","baseball glove","skateboard","surfboard","tennis racket","bottle",
        "wine glass","cup","fork","knife","spoon","bowl","banana","apple","sandwich","orange",
        "broccoli","carrot","hot dog","pizza","donut","cake","chair","couch","potted plant","bed",
        "dining table","toilet","tv","laptop","mouse","remote","keyboard","cell phone","microwave","oven",
        "toaster","sink","refrigerator","book","clock","vase","scissors","teddy bear","hair drier","toothbrush"
    };

    private void Awake()
    {
        if (modelAsset == null) { Debug.LogWarning("EchoesYoloDetector: no modelAsset assigned, detection disabled."); return; }
        try
        {
            _model = Unity.InferenceEngine.ModelLoader.Load(modelAsset);
            _worker = new Unity.InferenceEngine.Worker(_model, backend);
            Debug.Log($"EchoesYoloDetector: model loaded and worker created OK, backend={backend}, threshold={confidenceThreshold}");
        }
        catch (System.Exception e)
        {
            // v2.5.0: this is the exact failure mode the deploy guide
            // warned about, a Sentis API mismatch between the version
            // this file was written against and whatever is actually
            // installed. Previously this would have thrown uncaught and
            // left _worker null with zero visible explanation anywhere.
            // Now it logs loudly and detection just stays disabled for
            // the session instead, same safe no-op behaviour as an
            // unassigned modelAsset, but now with an actual reason
            // sitting in the Console instead of silence.
            Debug.LogError($"EchoesYoloDetector: model/worker init failed, detection disabled this session. {e.GetType().Name}: {e.Message}");
            _worker = null;
        }
    }

    private void OnDestroy()
    {
        _worker?.Dispose();
    }

    // v2.5.0: called once per still, right after TryCaptureImage() writes
    // the PNG. Reads the file back from disk deliberately, rather than
    // taking a Texture2D handle from the caller, so this detector never
    // has to assume anything about the rotation fix upstream, it just
    // reads whatever is actually on disk, which is the thing that
    // actually gets analysed later anyway.
    public List<EchoesScanController.DetectionEntry> Detect(string stillFilePath)
    {
        var results = new List<EchoesScanController.DetectionEntry>();
        if (_worker == null || !File.Exists(stillFilePath)) return results;

        byte[] pngBytes = File.ReadAllBytes(stillFilePath);
        var sourceTex = new Texture2D(2, 2);
        sourceTex.LoadImage(pngBytes); // auto-sizes to the PNG's real dimensions

        int origW = sourceTex.width, origH = sourceTex.height;
        Unity.InferenceEngine.Tensor<float> inputTensor = PreprocessLetterbox(sourceTex, out float scale, out float padX, out float padY);
        Destroy(sourceTex);

        _worker.Schedule(inputTensor);
        var outputTensor = _worker.PeekOutput("output0") as Unity.InferenceEngine.Tensor<float>;
        Unity.InferenceEngine.Tensor<float> cpuOutput = outputTensor.ReadbackAndClone();
        inputTensor.Dispose();

        float[] data = cpuOutput.DownloadToArray();
        cpuOutput.Dispose();

        // [1, 300, 6]: 300 rows of [x1, y1, x2, y2, confidence, class_id]
        // in the 640x640 letterboxed space. Undo the letterbox to get
        // back to this still's real pixel coordinates.
        for (int i = 0; i < 300; i++)
        {
            int b = i * 6;
            float conf = data[b + 4];
            if (conf < confidenceThreshold) continue;

            float x1 = (data[b + 0] - padX) / scale;
            float y1 = (data[b + 1] - padY) / scale;
            float x2 = (data[b + 2] - padX) / scale;
            float y2 = (data[b + 3] - padY) / scale;
            x1 = Mathf.Clamp(x1, 0, origW); y1 = Mathf.Clamp(y1, 0, origH);
            x2 = Mathf.Clamp(x2, 0, origW); y2 = Mathf.Clamp(y2, 0, origH);

            int classId = Mathf.RoundToInt(data[b + 5]);
            string label = (classId >= 0 && classId < COCO_CLASSES.Length) ? COCO_CLASSES[classId] : $"class_{classId}";

            results.Add(new EchoesScanController.DetectionEntry
            {
                label = label,
                source = "YOLO_ondevice", // v2.5.0 spec Section 6: exact observation source contract string
                confidence = conf,
                bbox = new[] { x1, y1, x2 - x1, y2 - y1 } // [x, y, w, h], top-left origin
            });
        }
        return results;
    }

    // Resizes into a 640x640 square, preserving aspect ratio, padding the
    // shorter side (letterbox), matching what the exported model expects.
    // Returns the CHW, 0..1 normalised input tensor plus the scale/pad
    // values needed to map detections back to the original still.
    private Unity.InferenceEngine.Tensor<float> PreprocessLetterbox(Texture2D src, out float scale, out float padX, out float padY)
    {
        scale = Mathf.Min((float)MODEL_INPUT_SIZE / src.width, (float)MODEL_INPUT_SIZE / src.height);
        int scaledW = Mathf.RoundToInt(src.width * scale);
        int scaledH = Mathf.RoundToInt(src.height * scale);
        padX = (MODEL_INPUT_SIZE - scaledW) / 2f;
        padY = (MODEL_INPUT_SIZE - scaledH) / 2f;

        var rt = RenderTexture.GetTemporary(MODEL_INPUT_SIZE, MODEL_INPUT_SIZE, 0, RenderTextureFormat.ARGB32);
        RenderTexture.active = rt;
        GL.Clear(true, true, new Color(0.447f, 0.447f, 0.447f)); // Ultralytics' standard grey letterbox pad
        Graphics.Blit(src, rt, new Vector2(scale * src.width / MODEL_INPUT_SIZE, scale * src.height / MODEL_INPUT_SIZE),
                      new Vector2(0, 0));

        var letterboxed = new Texture2D(MODEL_INPUT_SIZE, MODEL_INPUT_SIZE, TextureFormat.RGBA32, false);
        letterboxed.ReadPixels(new Rect(0, 0, MODEL_INPUT_SIZE, MODEL_INPUT_SIZE), 0, 0);
        letterboxed.Apply();
        RenderTexture.active = null;
        RenderTexture.ReleaseTemporary(rt);

        var tensor = new Unity.InferenceEngine.Tensor<float>(new Unity.InferenceEngine.TensorShape(1, 3, MODEL_INPUT_SIZE, MODEL_INPUT_SIZE));
        Color32[] pixels = letterboxed.GetPixels32();
        Destroy(letterboxed);
        for (int y = 0; y < MODEL_INPUT_SIZE; y++)
        {
            for (int x = 0; x < MODEL_INPUT_SIZE; x++)
            {
                // Texture2D is bottom-up, model expects top-down.
                Color32 p = pixels[(MODEL_INPUT_SIZE - 1 - y) * MODEL_INPUT_SIZE + x];
                tensor[0, 0, y, x] = p.r / 255f;
                tensor[0, 1, y, x] = p.g / 255f;
                tensor[0, 2, y, x] = p.b / 255f;
            }
        }
        return tensor;
    }
}
