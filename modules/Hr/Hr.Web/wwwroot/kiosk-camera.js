// Webcam QR scanning for the attendance kiosk.
//
// The frame is decoded on the server. Browsers disagree about whether they have a
// barcode decoder at all, and the alternative is shipping a third-party one into
// the page; a small JPEG over a LAN is the cheaper trade.
window.kioskCamera = (() => {
    let stream = null;
    let timer = null;
    let busy = false;

    // Fast enough to feel instant when someone holds a phone up, slow enough that
    // the server is not decoding frames it will never need.
    const INTERVAL_MS = 400;

    // Downscaled hard: a QR fills enough of the frame at this size to decode, and
    // it keeps each post to a few kilobytes.
    const CAPTURE_WIDTH = 640;

    async function start(videoEl, canvasEl, token, dotnet) {
        if (stream) return true;

        try {
            stream = await navigator.mediaDevices.getUserMedia({
                video: { facingMode: 'environment', width: { ideal: CAPTURE_WIDTH } },
                audio: false
            });
        } catch (e) {
            // Denied, already in use, or no camera. The page says so; the hardware
            // scanner still works either way.
            return false;
        }

        videoEl.srcObject = stream;
        await videoEl.play();

        timer = setInterval(() => grab(videoEl, canvasEl, token, dotnet), INTERVAL_MS);
        return true;
    }

    async function grab(videoEl, canvasEl, token, dotnet) {
        // Skip rather than queue: a backlog of stale frames is worse than a gap.
        if (busy || !videoEl.videoWidth) return;
        busy = true;

        try {
            const scale = CAPTURE_WIDTH / videoEl.videoWidth;
            canvasEl.width = CAPTURE_WIDTH;
            canvasEl.height = Math.round(videoEl.videoHeight * scale);
            canvasEl.getContext('2d').drawImage(videoEl, 0, 0, canvasEl.width, canvasEl.height);

            const blob = await new Promise(r => canvasEl.toBlob(r, 'image/jpeg', 0.6));
            if (!blob) return;

            const res = await fetch(`/hr/kiosk/${encodeURIComponent(token)}/frame`, {
                method: 'POST',
                headers: { 'Content-Type': 'image/jpeg' },
                body: blob
            });

            if (res.status !== 200) return;   // 204 = nothing in shot, the usual answer
            const { text } = await res.json();
            if (text) await dotnet.invokeMethodAsync('OnCameraScan', text);
        } catch {
            // A dropped frame is not worth reporting; the next one is 400ms away.
        } finally {
            busy = false;
        }
    }

    function stop(videoEl) {
        if (timer) { clearInterval(timer); timer = null; }
        if (stream) { stream.getTracks().forEach(t => t.stop()); stream = null; }
        if (videoEl) videoEl.srcObject = null;
    }

    return { start, stop };
})();
