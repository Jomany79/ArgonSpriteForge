(() => {
  let currentProject = null;
  let selectedIdx = 0;
  let isPlaying = false;
  let pingPong = false;
  let playDir = 1;
  let onionSkin = false;
  let loupeActive = false;
  let showPivot = true;
  let showRaw = false;
  let fps = 10;
  let lastTick = 0;
  let pollTimer = null;

  const imageCache = new Map();

  const $ = (id) => document.getElementById(id);
  const stageCanvas = $("stageCanvas");
  const stageCtx = stageCanvas.getContext("2d");
  const loupeCanvas = $("loupeCanvas");
  const loupeCtx = loupeCanvas.getContext("2d");

  $("apiKeyInput").value = localStorage.getItem("argon_api_key") || "";
  $("directorModel").value = localStorage.getItem("argon_director_model") || "gemini-2.5-flash";
  $("artistModel").value = localStorage.getItem("argon_artist_model") || "gemini-2.5-flash-image";

  $("apiKeyInput").addEventListener("change", (e) => localStorage.setItem("argon_api_key", e.target.value));
  $("directorModel").addEventListener("change", (e) => localStorage.setItem("argon_director_model", e.target.value));
  $("artistModel").addEventListener("change", (e) => localStorage.setItem("argon_artist_model", e.target.value));

  async function apiFetch(url, opts = {}) {
    const res = await fetch(url, opts);
    if (!res.ok) {
      let msg = `HTTP ${res.status}`;
      try {
        const j = await res.json();
        msg = j.detail || msg;
      } catch (_) {}
      throw new Error(msg);
    }
    return res.json();
  }

  function frameUrl(proj, fr, rawMode = false) {
    const rel = rawMode ? fr.raw_file : (fr.clean_file || fr.raw_file);
    if (!rel) return null;
    return `/projects-files/${proj.project_id}/${rel}?v=${fr.updated_at || 0}`;
  }

  function loadImg(url) {
    if (!url) return null;
    if (imageCache.has(url)) {
      const im = imageCache.get(url);
      return im.complete && im.naturalWidth > 0 ? im : null;
    }
    const im = new Image();
    im.onload = () => renderStage();
    im.src = url;
    imageCache.set(url, im);
    return null;
  }

  async function refreshCapabilities() {
    try {
      const cap = await apiFetch("/api/capabilities");
      if (cap.has_env_api_key) {
        $("envKeyBadge").textContent = "ENV ключ найден";
        $("envKeyBadge").style.color = "var(--ok)";
      }
      const sel = $("cvOnnxModel");
      const prev = sel.value;
      sel.innerHTML = "";
      const models = cap.onnx_models || [];
      if (models.length === 0) {
        const opt = document.createElement("option");
        opt.value = "";
        opt.textContent = "— Положите u2net.onnx в models/ —";
        sel.appendChild(opt);
      } else {
        for (const m of models) {
          const opt = document.createElement("option");
          opt.value = m;
          opt.textContent = m;
          sel.appendChild(opt);
        }
        if (prev && models.includes(prev)) sel.value = prev;
      }
    } catch (_) {}
  }

  async function refreshProjectsList(selectId = null) {
    const data = await apiFetch("/api/projects");
    const sel = $("projectSelect");
    const prev = selectId || sel.value;
    sel.innerHTML = '<option value="">— Выберите или создайте проект —</option>';
    for (const p of data.projects) {
      const opt = document.createElement("option");
      opt.value = p.project_id;
      opt.textContent = `${p.name} (${p.frame_count} к.)`;
      sel.appendChild(opt);
    }
    if (prev) sel.value = prev;
  }

  async function loadProject(pid) {
    if (!pid) {
      currentProject = null;
      renderAll();
      return;
    }
    currentProject = await apiFetch(`/api/projects/${pid}`);
    selectedIdx = Math.min(selectedIdx, Math.max(0, currentProject.frames.length - 1));
    syncControlsFromProject();
    renderAll();
    if (currentProject.job_running) startPolling();
  }

  function syncControlsFromProject() {
    if (!currentProject) return;
    const p = currentProject;
    $("projIdLabel").textContent = p.project_id;
    $("refPreview").src = `/projects-files/${p.project_id}/${p.reference_file}`;
    $("refPreview").style.display = "block";
    $("refPlaceholder").textContent = "Загрузить новый эталон (создаст новый проект)";

    $("motionPreset").value = p.motion_preset;
    $("customMotion").value = p.custom_motion || "";
    $("frameCount").value = String(p.frame_count || 8);
    $("optShadowless").checked = !!p.shadowless;
    $("optWingless").checked = !!p.wingless;
    $("optTopLight").checked = !!p.fixed_top_light;

    fps = p.fps || 10;
    $("fpsSlider").value = fps;
    $("fpsVal").textContent = fps;

    const cv = p.cv_settings;
    $("cvWidth").value = cv.cell_width;
    $("cvHeight").value = cv.cell_height;
    $("cvBgMethod").value = cv.bg_method;
    if (cv.onnx_model) $("cvOnnxModel").value = cv.onnx_model;
    $("cvAnchor").value = cv.anchor_mode;
    $("cvFill").value = cv.target_fill_ratio;
    $("cvAlpha").value = cv.alpha_threshold;
    $("cvShadow").value = cv.shadow_cut;
    $("cvFillHoles").checked = !!cv.fill_holes;
    $("cvPrimaryOnly").checked = !!cv.keep_primary_only;

    if (p.passport && p.passport.summary) {
      const locks = (p.passport.anatomy_locks || []).map((x) => `• ${escapeHtml(x)}`).join("<br>");
      $("passportBox").innerHTML = `<strong>Паспорт персонажа:</strong><br>${escapeHtml(p.passport.summary)}<br>${locks}`;
      $("passportBox").style.display = "block";
    } else {
      $("passportBox").style.display = "none";
    }

    const maxDim = 400;
    const scale = Math.min(maxDim / cv.cell_width, maxDim / cv.cell_height, 1.5);
    stageCanvas.width = Math.round(cv.cell_width * scale);
    stageCanvas.height = Math.round(cv.cell_height * scale);
  }

  function renderAll() {
    renderTimeline();
    renderInspector();
    renderLogs();
    renderJobStatus();
    renderStage();
  }

  function renderJobStatus() {
    if (!currentProject) return;
    const pct = Math.round((currentProject.job_progress || 0) * 100);
    $("jobProgressFill").style.width = `${pct}%`;
    $("jobStageText").textContent = `Статус: ${currentProject.job_stage} (${pct}%)`;
    $("btnRunGenerate").disabled = !!currentProject.job_running;
    $("btnRunDirector").disabled = !!currentProject.job_running;
  }

  function renderLogs() {
    const box = $("logsBox");
    if (!currentProject) {
      box.innerHTML = "[ARGON HIGH // .NET 8] Загрузите эталонное изображение слева, чтобы создать проект.";
      return;
    }
    box.innerHTML = (currentProject.logs || []).map((l) => `<div>${escapeHtml(l)}</div>`).join("");
    box.scrollTop = box.scrollHeight;
  }

  function escapeHtml(s) {
    return String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
  }

  function renderTimeline() {
    const strip = $("timelineStrip");
    strip.innerHTML = "";
    if (!currentProject || !currentProject.frames.length) {
      $("timelineCount").textContent = "0";
      return;
    }
    $("timelineCount").textContent = String(currentProject.frames.length);

    currentProject.frames.forEach((fr, idx) => {
      const card = document.createElement("div");
      card.className = "frame-card" + (idx === selectedIdx ? " selected" : "") + (!fr.adjustments.enabled ? " disabled" : "");
      card.onclick = () => {
        selectedIdx = idx;
        renderTimeline();
        renderInspector();
        renderStage();
      };

      const thumbWrap = document.createElement("div");
      thumbWrap.className = "frame-thumb-wrap bg-checker";
      const u = frameUrl(currentProject, fr, false);
      if (u) {
        const img = document.createElement("img");
        img.className = "frame-thumb";
        img.src = u;
        thumbWrap.appendChild(img);
        loadImg(u);
      } else {
        thumbWrap.innerHTML = '<span class="hint">План</span>';
      }

      const meta = document.createElement("div");
      meta.className = "frame-meta";
      const dotClass = `qa-${fr.qa.status || "pending"}`;
      meta.innerHTML = `<span>${escapeHtml(fr.label)}</span><span class="qa-dot ${dotClass}" title="QA: ${fr.qa.status}"></span>`;

      card.appendChild(thumbWrap);
      card.appendChild(meta);
      strip.appendChild(card);
    });
  }

  function renderInspector() {
    if (!currentProject || !currentProject.frames[selectedIdx]) {
      $("selFrameBadge").textContent = "—";
      $("qaStatus").textContent = "—";
      $("qaMad").textContent = "—";
      $("qaMargins").textContent = "—";
      $("qaAiScore").textContent = "—";
      $("framePromptInput").value = "";
      return;
    }
    const fr = currentProject.frames[selectedIdx];
    $("selFrameBadge").textContent = fr.label;
    $("activeFrameInfo").textContent = `Кадр ${fr.index + 1}/${currentProject.frames.length} — ${fr.label}`;

    $("qaStatus").textContent = (fr.qa.status || "pending").toUpperCase();
    $("qaStatus").style.color =
      fr.qa.status === "ok" ? "var(--ok)" : fr.qa.status === "warn" ? "var(--warn)" : fr.qa.status === "fail" ? "var(--fail)" : "var(--muted)";
    $("qaMad").textContent = fr.qa.mad_prev !== null && fr.qa.mad_prev !== undefined ? fr.qa.mad_prev : "—";
    const m = fr.qa.margins || {};
    $("qaMargins").textContent = m.top !== undefined ? `${m.top}/${m.bottom}/${m.left}/${m.right}` : "—";
    $("qaAiScore").textContent = fr.qa.ai_score !== null && fr.qa.ai_score !== undefined ? `${fr.qa.ai_score}/100` : "—";

    if (fr.qa.ai_notes) {
      $("qaAiNotes").textContent = `AI QA: ${fr.qa.ai_notes}`;
      $("qaAiNotes").style.display = "block";
    } else {
      $("qaAiNotes").style.display = "none";
    }

    $("framePromptInput").value = fr.prompt || "";
    const adj = fr.adjustments;
    $("adjEnabled").checked = adj.enabled !== false;
    $("adjDx").value = adj.dx || 0;
    $("adjDy").value = adj.dy || 0;
    $("adjScale").value = adj.scale || 1.0;
    $("adjRot").value = adj.rotation_deg || 0;
    $("adjAlpha").value = adj.alpha_threshold_override ?? "";
    $("adjShadow").value = adj.shadow_cut_override ?? "";
  }

  function renderStage() {
    const W = stageCanvas.width, H = stageCanvas.height;
    stageCtx.clearRect(0, 0, W, H);

    if (!currentProject || !currentProject.frames.length) {
      stageCtx.fillStyle = "rgba(255,255,255,0.45)";
      stageCtx.font = "13px system-ui";
      stageCtx.textAlign = "center";
      stageCtx.fillText("Загрузите эталон и нажмите «Спланировать» или «Нарезать»", W / 2, H / 2);
      return;
    }

    const frames = currentProject.frames;
    const fr = frames[selectedIdx];
    if (!fr) return;

    if (onionSkin && frames.length > 1) {
      const prevIdx = (selectedIdx - 1 + frames.length) % frames.length;
      const nextIdx = (selectedIdx + 1) % frames.length;
      const prevImg = loadImg(frameUrl(currentProject, frames[prevIdx], showRaw));
      const nextImg = loadImg(frameUrl(currentProject, frames[nextIdx], showRaw));

      if (prevImg) {
        stageCtx.save();
        stageCtx.globalAlpha = 0.34;
        stageCtx.filter = "sepia(1) hue-rotate(150deg) saturate(2.5)";
        stageCtx.drawImage(prevImg, 0, 0, W, H);
        stageCtx.restore();
      }
      if (nextImg && nextIdx !== prevIdx) {
        stageCtx.save();
        stageCtx.globalAlpha = 0.22;
        stageCtx.filter = "sepia(1) hue-rotate(280deg) saturate(2.5)";
        stageCtx.drawImage(nextImg, 0, 0, W, H);
        stageCtx.restore();
      }
    }

    const curImg = loadImg(frameUrl(currentProject, fr, showRaw));
    if (curImg) {
      stageCtx.drawImage(curImg, 0, 0, W, H);
    } else {
      stageCtx.fillStyle = "rgba(255,255,255,0.5)";
      stageCtx.font = "13px system-ui";
      stageCtx.textAlign = "center";
      stageCtx.fillText(`Кадр ${fr.label} ещё не отрендерен`, W / 2, H / 2);
    }

    if (showPivot && currentProject.cv_settings) {
      const cv = currentProject.cv_settings;
      const px = W * cv.pivot_x_ratio;
      const py = cv.anchor_mode === "bottom_center"
        ? H * (0.5 + cv.target_fill_ratio * 0.5)
        : (cv.anchor_mode === "thoracic_pivot" ? H * cv.pivot_y_ratio : H * 0.5);

      stageCtx.save();
      stageCtx.strokeStyle = "rgba(0, 229, 255, 0.7)";
      stageCtx.lineWidth = 1;
      stageCtx.setLineDash([4, 4]);
      stageCtx.beginPath();
      stageCtx.moveTo(px, 0); stageCtx.lineTo(px, H);
      stageCtx.moveTo(0, py); stageCtx.lineTo(W, py);
      stageCtx.stroke();
      stageCtx.setLineDash([]);
      stageCtx.fillStyle = "#ffca28";
      stageCtx.beginPath();
      stageCtx.arc(px, py, 3.5, 0, Math.PI * 2);
      stageCtx.fill();
      stageCtx.restore();
    }
  }

  function animLoop(ts) {
    if (isPlaying && currentProject && currentProject.frames.length > 1) {
      const interval = 1000 / Math.max(1, fps);
      if (ts - lastTick >= interval) {
        lastTick = ts;
        const enabledIndices = currentProject.frames
          .map((f, i) => (f.adjustments.enabled && (f.clean_file || f.raw_file) ? i : -1))
          .filter((i) => i >= 0);
        if (enabledIndices.length > 1) {
          let pos = enabledIndices.indexOf(selectedIdx);
          if (pos === -1) pos = 0;
          if (pingPong) {
            pos += playDir;
            if (pos >= enabledIndices.length) { playDir = -1; pos = enabledIndices.length - 2; }
            else if (pos < 0) { playDir = 1; pos = 1; }
          } else {
            pos = (pos + 1) % enabledIndices.length;
          }
          selectedIdx = enabledIndices[pos];
          renderTimeline();
          renderInspector();
          renderStage();
        }
      }
    }
    requestAnimationFrame(animLoop);
  }
  requestAnimationFrame(animLoop);

  stageCanvas.addEventListener("mousemove", (e) => {
    if (!loupeActive) { loupeCanvas.style.display = "none"; return; }
    const rect = stageCanvas.getBoundingClientRect();
    const areaRect = $("stageArea").getBoundingClientRect();
    const mx = e.clientX - rect.left;
    const my = e.clientY - rect.top;
    const lw = loupeCanvas.width, lh = loupeCanvas.height;
    const zoom = 4;

    loupeCanvas.style.display = "block";
    loupeCanvas.style.left = `${e.clientX - areaRect.left + 18}px`;
    loupeCanvas.style.top = `${e.clientY - areaRect.top - lh / 2}px`;

    loupeCtx.clearRect(0, 0, lw, lh);
    loupeCtx.fillStyle = "#161b22";
    loupeCtx.fillRect(0, 0, lw, lh);
    loupeCtx.imageSmoothingEnabled = false;
    loupeCtx.drawImage(
      stageCanvas,
      mx - lw / (2 * zoom), my - lh / (2 * zoom), lw / zoom, lh / zoom,
      0, 0, lw, lh
    );
    loupeCtx.strokeStyle = "rgba(255,202,40,0.85)";
    loupeCtx.lineWidth = 1;
    loupeCtx.beginPath();
    loupeCtx.moveTo(lw / 2, 0); loupeCtx.lineTo(lw / 2, lh);
    loupeCtx.moveTo(0, lh / 2); loupeCtx.lineTo(lw, lh / 2);
    loupeCtx.stroke();
  });
  stageCanvas.addEventListener("mouseleave", () => { loupeCanvas.style.display = "none"; });

  function startPolling() {
    if (pollTimer) clearInterval(pollTimer);
    pollTimer = setInterval(async () => {
      if (!currentProject) { clearInterval(pollTimer); return; }
      try {
        currentProject = await apiFetch(`/api/projects/${currentProject.project_id}`);
        renderAll();
        if (!currentProject.job_running) {
          clearInterval(pollTimer);
          pollTimer = null;
          refreshProjectsList(currentProject.project_id);
        }
      } catch (_) {}
    }, 700);
  }

  async function handleReferenceUpload(file) {
    if (!file) return;
    const fd = new FormData();
    const pname = $("newProjectName").value.trim() || file.name.replace(/\.[^.]+$/, "");
    fd.append("name", pname);
    fd.append("file", file);
    try {
      currentProject = await apiFetch("/api/projects", { method: "POST", body: fd });
      selectedIdx = 0;
      $("newProjectName").value = "";
      await refreshProjectsList(currentProject.project_id);
      syncControlsFromProject();
      renderAll();
    } catch (err) {
      alert("Ошибка загрузки эталона: " + err.message);
    }
  }

  $("refDropZone").onclick = () => $("refFileInput").click();
  $("refFileInput").onchange = (e) => handleReferenceUpload(e.target.files[0]);
  $("refDropZone").addEventListener("dragover", (e) => { e.preventDefault(); });
  $("refDropZone").addEventListener("drop", (e) => {
    e.preventDefault();
    if (e.dataTransfer.files[0]) handleReferenceUpload(e.dataTransfer.files[0]);
  });

  $("projectSelect").onchange = (e) => loadProject(e.target.value);

  $("btnDeleteProject").onclick = async () => {
    if (!currentProject) return;
    if (!confirm(`Удалить проект «${currentProject.name}»?`)) return;
    await apiFetch(`/api/projects/${currentProject.project_id}`, { method: "DELETE" });
    currentProject = null;
    await refreshProjectsList("");
    renderAll();
  };

  $("btnRunDirector").onclick = async () => {
    if (!currentProject) return alert("Сначала загрузите эталонную картинку.");
    $("btnRunDirector").disabled = true;
    try {
      currentProject = await apiFetch(`/api/projects/${currentProject.project_id}/plan`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          motion_preset: $("motionPreset").value,
          custom_motion: $("customMotion").value,
          frame_count: parseInt($("frameCount").value, 10),
          wingless: $("optWingless").checked,
          shadowless: $("optShadowless").checked,
          fixed_top_light: $("optTopLight").checked,
          api_key: $("apiKeyInput").value,
          director_model: $("directorModel").value,
        }),
      });
      selectedIdx = 0;
      syncControlsFromProject();
      renderAll();
      refreshProjectsList(currentProject.project_id);
    } catch (err) {
      alert("Ошибка Агента 1: " + err.message);
    } finally {
      $("btnRunDirector").disabled = false;
    }
  };

  $("btnRunGenerate").onclick = async () => {
    if (!currentProject) return alert("Сначала создайте проект.");
    try {
      await apiFetch(`/api/projects/${currentProject.project_id}/generate`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          api_key: $("apiKeyInput").value,
          artist_model: $("artistModel").value,
          inspector_model: $("directorModel").value,
          use_prev_frame_anchor: $("optPrevAnchor").checked,
          run_ai_inspector: $("optAiQa").checked,
          auto_retry_failed: $("optAutoRetry").checked,
          max_retries: 1,
        }),
      });
      startPolling();
    } catch (err) {
      alert("Ошибка запуска генерации: " + err.message);
    }
  };

  $("btnSliceRef").onclick = async () => {
    if (!currentProject) return alert("Сначала загрузите эталон-лист (например, Hornet.jpg).");
    const fd = new FormData();
    fd.append("mode", $("sliceMode").value);
    fd.append("cols", $("sliceCols").value);
    fd.append("rows", $("sliceRows").value);
    fd.append("header_px", $("sliceHeader").value);
    fd.append("footer_px", $("sliceFooter").value);
    fd.append("use_reference", "true");
    try {
      currentProject = await apiFetch(`/api/projects/${currentProject.project_id}/slice-import`, {
        method: "POST",
        body: fd,
      });
      selectedIdx = 0;
      syncControlsFromProject();
      renderAll();
      refreshProjectsList(currentProject.project_id);
    } catch (err) {
      alert("Ошибка нарезки: " + err.message);
    }
  };

  $("btnRegenFrame").onclick = async () => {
    if (!currentProject || !currentProject.frames[selectedIdx]) return;
    $("btnRegenFrame").disabled = true;
    try {
      currentProject = await apiFetch(
        `/api/projects/${currentProject.project_id}/frames/${selectedIdx}/regenerate`,
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            api_key: $("apiKeyInput").value,
            artist_model: $("artistModel").value,
            inspector_model: $("directorModel").value,
            custom_prompt: $("framePromptInput").value,
            tweak_instruction: $("frameTweakInput").value,
            use_prev_frame_anchor: $("optPrevAnchor").checked,
            run_ai_inspector: $("optAiQa").checked,
          }),
        }
      );
      $("frameTweakInput").value = "";
      renderAll();
    } catch (err) {
      alert("Ошибка перегенерации кадра: " + err.message);
    } finally {
      $("btnRegenFrame").disabled = false;
    }
  };

  $("btnUploadFrame").onclick = () => $("singleFrameFileInput").click();
  $("singleFrameFileInput").onchange = async (e) => {
    if (!currentProject || !e.target.files[0]) return;
    const fd = new FormData();
    fd.append("file", e.target.files[0]);
    try {
      currentProject = await apiFetch(
        `/api/projects/${currentProject.project_id}/frames/${selectedIdx}/upload`,
        { method: "POST", body: fd }
      );
      renderAll();
    } catch (err) {
      alert("Ошибка загрузки кадра: " + err.message);
    }
  };

  $("btnApplyAdj").onclick = async () => {
    if (!currentProject || !currentProject.frames[selectedIdx]) return;
    const alphaOv = $("adjAlpha").value.trim() === "" ? null : parseFloat($("adjAlpha").value);
    const shadowOv = $("adjShadow").value.trim() === "" ? null : parseFloat($("adjShadow").value);
    try {
      currentProject = await apiFetch(
        `/api/projects/${currentProject.project_id}/frames/${selectedIdx}/adjust`,
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            dx: parseFloat($("adjDx").value) || 0,
            dy: parseFloat($("adjDy").value) || 0,
            scale: parseFloat($("adjScale").value) || 1.0,
            rotation_deg: parseFloat($("adjRot").value) || 0,
            alpha_threshold_override: alphaOv,
            shadow_cut_override: shadowOv,
            enabled: $("adjEnabled").checked,
          }),
        }
      );
      renderAll();
    } catch (err) {
      alert("Ошибка правки кадра: " + err.message);
    }
  };

  $("btnDeleteFrame").onclick = async () => {
    if (!currentProject || !currentProject.frames[selectedIdx]) return;
    currentProject = await apiFetch(
      `/api/projects/${currentProject.project_id}/frames/${selectedIdx}`,
      { method: "DELETE" }
    );
    selectedIdx = Math.max(0, Math.min(selectedIdx, currentProject.frames.length - 1));
    renderAll();
    refreshProjectsList(currentProject.project_id);
  };

  $("btnReprocessAll").onclick = async () => {
    if (!currentProject) return;
    $("btnReprocessAll").disabled = true;
    try {
      currentProject = await apiFetch(`/api/projects/${currentProject.project_id}/cv-settings`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          cell_width: parseInt($("cvWidth").value, 10) || 256,
          cell_height: parseInt($("cvHeight").value, 10) || 256,
          target_fill_ratio: parseFloat($("cvFill").value) || 0.80,
          anchor_mode: $("cvAnchor").value,
          pivot_x_ratio: 0.5,
          pivot_y_ratio: 0.48,
          bg_method: $("cvBgMethod").value,
          onnx_model: $("cvOnnxModel").value || "",
          alpha_threshold: parseFloat($("cvAlpha").value) || 14,
          alpha_softness: 14,
          shadow_cut: parseFloat($("cvShadow").value) || 0.35,
          fill_holes: $("cvFillHoles").checked,
          keep_primary_only: $("cvPrimaryOnly").checked,
          min_component_ratio: 0.015,
        }),
      });
      syncControlsFromProject();
      renderAll();
    } catch (err) {
      alert("Ошибка пересчёта CV/ONNX: " + err.message);
    } finally {
      $("btnReprocessAll").disabled = false;
    }
  };

  $("btnPlay").onclick = () => {
    isPlaying = !isPlaying;
    $("btnPlay").textContent = isPlaying ? "⏸ Пауза" : "▶ Играть";
  };
  $("btnPrev").onclick = () => {
    if (!currentProject || !currentProject.frames.length) return;
    selectedIdx = (selectedIdx - 1 + currentProject.frames.length) % currentProject.frames.length;
    renderTimeline(); renderInspector(); renderStage();
  };
  $("btnNext").onclick = () => {
    if (!currentProject || !currentProject.frames.length) return;
    selectedIdx = (selectedIdx + 1) % currentProject.frames.length;
    renderTimeline(); renderInspector(); renderStage();
  };
  $("btnPingPong").onclick = (e) => {
    pingPong = !pingPong;
    e.target.classList.toggle("active", pingPong);
  };
  $("fpsSlider").oninput = (e) => {
    fps = parseInt(e.target.value, 10);
    $("fpsVal").textContent = fps;
  };
  $("fpsSlider").onchange = () => {
    if (!currentProject) return;
    const fd = new FormData();
    fd.append("fps", String(fps));
    fetch(`/api/projects/${currentProject.project_id}/fps`, { method: "POST", body: fd });
  };
  $("btnOnion").onclick = (e) => {
    onionSkin = !onionSkin;
    e.target.classList.toggle("active", onionSkin);
    renderStage();
  };
  $("btnLoupe").onclick = (e) => {
    loupeActive = !loupeActive;
    e.target.classList.toggle("active", loupeActive);
    if (!loupeActive) loupeCanvas.style.display = "none";
  };
  $("btnPivotGuide").onclick = (e) => {
    showPivot = !showPivot;
    e.target.classList.toggle("active", showPivot);
    renderStage();
  };
  $("btnShowRaw").onclick = (e) => {
    showRaw = !showRaw;
    e.target.classList.toggle("active", showRaw);
    renderStage();
  };
  $("bgSelect").onchange = (e) => {
    const shell = $("canvasShell");
    shell.className = `canvas-shell ${e.target.value}`;
  };

  $("btnExportGrid").onclick = () => {
    if (!currentProject) return;
    window.location.href = `/api/projects/${currentProject.project_id}/export/spritesheet?layout=grid`;
  };
  $("btnExportStrip").onclick = () => {
    if (!currentProject) return;
    window.location.href = `/api/projects/${currentProject.project_id}/export/spritesheet?layout=strip`;
  };
  $("btnExportBundle").onclick = () => {
    if (!currentProject) return;
    window.location.href = `/api/projects/${currentProject.project_id}/export/bundle?layout=grid`;
  };

  $("btnOpenModels").onclick = async () => {
    try { await apiFetch("/api/open-models-folder", { method: "POST" }); } catch (e) { alert(e.message); }
  };
  $("btnRefreshModels").onclick = () => refreshCapabilities();
  $("btnOpenProjectDir").onclick = async () => {
    if (!currentProject) return;
    try { await apiFetch(`/api/projects/${currentProject.project_id}/open-folder`, { method: "POST" }); } catch (e) { alert(e.message); }
  };

  refreshCapabilities();
  refreshProjectsList();
})();
