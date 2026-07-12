# SidebarMonitor — Release Readiness & Platform Research

> Preparado durante la noche del 2026-07-11→12. Checklist accionable + investigación de SDKs
> (NVIDIA / Intel CPU / Intel GPU / AMD GPU) para decidir expansión. Marca casillas al ir cerrando.

---

## 0. TL;DR

- **Viable** como **open-source (MIT) + "cómprame un café" + proyecto-escaparate de CV.** Nicho **Ryzen (+ NVIDIA)**. No es un negocio: donaciones = céntimos.
- **Gancho de marketing regalado por el mercado:** *"el monitor lateral de Ryzen que SIGUE funcionando tras el apocalipsis de WinRing0"*. El competidor directo, [Sidebar Diagnostics](https://github.com/ArcadeRenegade/SidebarDiagnostics), está **roto** desde marzo-2025 porque usa WinRing0 (Defender lo marca `VulnerableDriver`, HVCI lo bloquea — [issue #475](https://github.com/ArcadeRenegade/SidebarDiagnostics/issues/475)). Nosotros lo evitamos por diseño.
- **Legal resuelto** (decisión de Rubén): **empaquetar el DLL de AMD + aceptar su EULA en el 1er arranque**, código **MIT**.

---

## 1. Legal — AMD Ryzen Master Monitoring SDK

Leída la EULA local (`C:\Program Files\AMD\RyzenMasterMonitoringSDK\License.rtf`). Título: *"Software Evaluation License Agreement (Object Code Only)"*.

**Lo que permite (Sec. 2):** distribuir el Software (DLL/driver, **object code only**) a terceros, siempre que **cada receptor acepte la EULA de AMD antes de usarlo** y se cumplan las restricciones. Otros lo hacen (HWiNFO, OCMaestro).

**Restricciones que nos afectan (Sec. 3):**
- ❌ No modificar/crear derivados del DLL, no reversear, no quitar avisos de copyright.
- ❌ **(3f) No usarlo de forma que requiera licenciarlo bajo una "Free Software License"** (= copyleft: GPL/LGPL/AGPL). → **Nuestro código debe ser MIT/BSD/Apache, NO GPL.**
- Indemnizas a AMD (liability tope $100). Reglas de export US. Marco "evaluation" = ⚠️ bandera amarilla si algún día se monetiza en serio (confirmar uso comercial con AMD + abogado).

**Camino elegido (bundle + first-run EULA):**
- [x] Incluir `License.rtf` de AMD en el instalador/app. → `fetch.ps1` la copia del SDK a `native/RyzenSdk/`.
- [x] **Diálogo de 1er arranque** que muestre la EULA de AMD y exija "Acepto" antes de arrancar el helper/SDK (guardar flag en config). Sin aceptar → la app funciona pero sin los sensores del SDK de AMD (degradar a PDH). → `FirstRunDialog.cs` (rama AMD/Intel según CPUID); consentimiento en `ui.json` + marcador `amd-sdk-consent` que el helper elevado lee y aplica en caliente.
- [x] `LICENSE` = **MIT** para el código propio.
- [x] `THIRD-PARTY-NOTICES.md` con atribución: AMD Ryzen Master Monitoring SDK, Microsoft VC++ redistributables, NVIDIA NVML, Microsoft.Diagnostics.Tracing (TraceEvent), etc.
- [ ] `fetch.ps1` actual empaqueta el DLL **desde la instalación del dev** → para release, documentar/scriptar que el instalador lo tome de una fuente válida y NUNCA meter los binarios de AMD en el repo público de git (un `git clone` no cumple la condición de "aceptar la EULA"). Los binarios van solo en el **instalador**, no en el repo.

---

## 2. Checklist de release

### P0 — bloqueantes (sin esto no se publica)
- [x] **EULA AMD en 1er arranque** + `License.rtf` incluida (ver §1).
- [x] **`LICENSE` MIT** + **`THIRD-PARTY-NOTICES.md`**.
- [~] **Degradación elegante** — que NO casque ni se vea roto en: *(código listo y con guards; falta validar en hardware real)*
  - [x] CPU Intel (sin SDK de AMD) → `CpuVendor` (CPUID) detecta Intel; el helper NO abre el SDK de AMD; UI muestra "—" en temp/vatios, throttle/límites/boost-mejor-núcleo/estrella se ocultan (gateados por `TjMaxC>0`/`BestCore>=0`/`CpuFromAmd`); el 1er arranque explica que hace falta ring0.
  - [x] Sin GPU NVIDIA (NVML ausente) → GPU por D3DKMT/engines; la fila de temp/W/VRAM se omite (`HasDetail=0`).
  - [x] Sin helper elevado → sin ETW/temp por core/C0, resto vivo (`CpuFromAmd=false`, `BestCore=-1`).
  - [ ] Nº de cores distinto (4/6/12/16/24…), sin SMT, multi-CCD. *(CSV y filas por-núcleo ya son dinámicos por CoreCount)*
  - [ ] Multi-monitor, apagar/encender monitores (ya arreglado), DPI 100/150/200%.
  - [ ] **Probar en ≥1 equipo Intel y ≥1 sin NVIDIA** → **Rubén tiene portátil Ryzen mobile+RTX 3050 y equipo Intel 7700K para probar.**
- [ ] **Firma de código (Authenticode)** → evita el susto de SmartScreen. Gratis para OSS: [SignPath](https://signpath.io/) o [Azure Trusted Signing](https://learn.microsoft.com/azure/trusted-signing/).
- [ ] **Nombre/marca**: verificar que "SidebarMonitor" no colisiona (hay "Sidebar Diagnostics", "System Monitor II"…). Icono definitivo + `AppUserModelID` correcto.

### P1 — importante para adopción
- [ ] **UI en inglés** (i18n; mantener español). Es un mercado global.
- [ ] **Ventana de ajustes** de verdad (el menú contextual actual es potente pero denso para un recién llegado).
- [ ] **Instalador** propio (Inno Setup / WiX-MSI) con: EULA AMD, autostart opcional, tarea del helper elevado, desinstalación limpia. + **manifiesto winget**. Valorar **Microsoft Store** (canal de pago de baja fricción: 2-5 €).
- [ ] **Autoactualización** o al menos aviso de versión nueva desde GitHub Releases (comparar con `AppVersion`).
- [ ] **README** con el pitch anti-WinRing0, tabla de features, requisitos (Ryzen para CPU-deep, NVIDIA para GPU-full), **capturas + GIF de demo**.
- [ ] **Landing** (GitHub Pages) + botón **café** (GitHub Sponsors / Buy Me a Coffee).
- [ ] **Política sin telemetría**, dicho explícito (los entusiastas lo valoran).
- [ ] **CI** (GitHub Actions): build AOT del agente + publish + firmar + crear Release. (Ojo: el AOT necesita el toolchain C++ y `vswhere` en PATH.)
- [ ] **Docs**: cómo funciona (agente AOT + helper ETW + shim SDK), FAQ ("¿por qué necesita el SDK de AMD?", "¿por qué un helper elevado?", "¿telemetría? no").

### P2 — nice-to-have / futuro
- [ ] Modo portable (sin instalar).
- [ ] Más idiomas.
- [ ] Expansión de plataformas (ver §3).
- [x] ~~Logging / exportar CSV~~ → **hecho y en el core gratis** (menú *Diagnóstico → Registrar a CSV*: 1 fila/muestra, 85 cols, por-núcleo dinámico, a `%LOCALAPPDATA%\SidebarMonitor\logs`). + **overlay verbose** (*Diagnóstico → Datos de depuración*) y flags `--verbose`/`--csv`.
- [ ] Pestaña "Pro" opcional si se busca monetizar (extras: alertas, umbrales). *(logging/CSV ya no es Pro)*

---

## 3. Investigación de SDKs — ¿se puede expandir sin el problema del driver?

**Regla de oro:** un SDK que **viene con el driver del fabricante** (NVML, ADLX, IGCL) = **limpio** (firmado, HVCI-safe, sin bundlear driver propio). Leer MSR/registros directamente = **ring0** = el problema de WinRing0.

### 3.1 GPUs — expansión LIMPIA y tri-vendor ✅

| Vendor | SDK | Da | Distribución |
|---|---|---|---|
| **NVIDIA** | **NVML** (ya usado) | uso adapter **y por proceso** (`nvmlDeviceGetProcessUtilization`: gpu/mem/enc/dec %), temp (1 sensor), W, clocks, fan, VRAM | `nvml.dll` **viene con el driver** → sin redistribución |
| **AMD** | **ADLX** ([GPUOpen](https://gpuopen.com/adlx/)) — *no lo usamos aún* | temp, **W**, fan, VRAM, clocks, tuning | `.dll` **viene con el driver** Radeon → sin redistribución |
| **Intel** | **IGCL** ([repo](https://github.com/intel/drivers.gpu.control-library), [docs](https://intel.github.io/drivers.gpu.control-library/)) | temp (core/mem/global), W, fan, freq, engines, memoria | binarios **vienen con el driver** Intel; headers en GitHub. 64-bit. Sobre Level Zero Sysman |

- **NVAPI** (alternativa NVIDIA): solo adapter, pero hasta 3 sensores térmicos. También viene con el driver.
- **Conclusión GPU:** soporte completo **NVIDIA + AMD + Intel es factible y limpio**, todo ships-with-driver. **Ventaja fuerte y diferenciadora.** Hoy solo NVIDIA está a full; AMD/Intel GPU solo tienen motores (D3DKMT).

### 3.2 Intel CPU — el hueco DIFÍCIL ⚠️

- **Intel NO tiene un SDK de monitorización de CPU equivalente a Ryzen Master.** No hay "driver firmado que te da temp/W" oficial de consumo.
- **Intel Power Gadget: DEPRECADO** (fin de vida dic-2023).
- Reemplazo que sugiere Intel: **[Intel PCM](https://github.com/intel/pcm)** (open-source) — pero lee **MSR/PCI ⇒ necesita driver ring0**. Da *thermal headroom* (distancia al Tjmax), no temperatura absoluta.
- Temperatura Intel = **DTS** vía MSR `IA32_THERM_STATUS`. Potencia = **RAPL** MSR. Ambos = ring0.
- **La vía limpia moderna: [PawnIO](https://poorlydocumented.com/2025/09/replacing-winring0-in-fan-control-with-pawnio/) + módulos MSR** — driver **firmado, HVCI-safe, no blocklisted**. **[CapFrameX](https://github.com/CXWorld/CapFrameX) ya lo usa** ("PawnIO wrapper for MSR and OC mailbox with updated Intel MSR IDs").
- **Conclusión Intel CPU:** posible, **pero con dependencia de PawnIO** (instalación aparte, más trabajo, ecosistema joven). NO tan limpio como AMD (cuyo SDK es autocontenido). **Refuerza el posicionamiento "Ryzen-first".**

### 3.3 Matriz resumen

| Componente | Vía limpia (ships-with-driver / HVCI) | ¿Dependencia de driver propio? | Estado |
|---|---|---|---|
| GPU NVIDIA | ✅ NVML | No (dll del sistema) | **Hecho** |
| GPU AMD | ✅ ADLX | No (dll del sistema) | Pendiente (hoy solo engines D3DKMT) |
| GPU Intel | ✅ IGCL | No (dll del sistema) | Pendiente |
| CPU AMD | ✅ Ryzen Master SDK | Sí, pero **firmado + HVCI-safe** (empaquetado) | **Hecho** |
| CPU Intel | ⚠️ PawnIO + MSR | Sí — PawnIO (firmado, HVCI-safe, **instalación aparte**) | No; lift grande |

### 3.4 Recomendación de expansión (por ROI)

1. **ADLX** (AMD GPU: temp/W/fan) — **bajo esfuerzo, alto valor**: completa la **iGPU del Ryzen** y cualquier Radeon dGPU sin driver nuevo. Encaja con el foco AMD.
2. **IGCL** (Intel Arc) — esfuerzo medio, limpio, abre el mercado Intel-GPU.
3. **NVML por-proceso** — pulir el "qué proceso usa la GPU" en NVIDIA con `nvmlDeviceGetProcessUtilization` (más preciso que el PDH GPU Engine actual, que es la vía neutral — mantener PDH como fallback).
4. **CPU Intel vía PawnIO** — **solo si el mercado Intel lo pide**. Mayor esfuerzo + dependencia de driver aparte. Mantén **AMD como la experiencia premium** ("hecho para Ryzen").

---

## 4. Monetización (recap honesto)

- **Donaciones puras** (GitHub Sponsors / Buy Me a Coffee): realista **$0-500/mes**, mediana **$0**. Cafés y buena voluntad, no sueldo.
- **PPI / bundleware:** más dinero pero **envenena la confianza** del público entusiasta. **No.**
- **Menos malo si se busca dinero:** listado barato en **Microsoft Store** (comodidad + autoupdate) y/o un **"Pro"** con extras. Mantener el core gratis/OSS.
- **ROI real:** **reputación + portfolio** (NativeAOT, ETW, SeqLock en memoria compartida, interop nativo, HVCI-compatible). Vale más que las donaciones.

---

## 5. Posicionamiento (el pitch)

> **SidebarMonitor** — barra lateral de monitorización **nativa y eficiente** para Windows 11, **hecha para Ryzen + NVIDIA**, que **sigue funcionando con Integridad de Memoria (HVCI)** activa — donde Sidebar Diagnostics y las herramientas basadas en WinRing0 se rompieron en 2025. Sin HWiNFO, sin drivers dudosos: SDK oficial de AMD + APIs nativas de Windows + NVML.

Dónde anunciarlo: r/AMD, r/overclocking, r/pcmasterrace, el hilo de gente que perdió Sidebar Diagnostics, foros de Ryzen.

---

## 6. Fuentes

- Sidebar Diagnostics (competidor): https://github.com/ArcadeRenegade/SidebarDiagnostics · issue WinRing0: https://github.com/ArcadeRenegade/SidebarDiagnostics/issues/475
- WinRing0 blocklist: https://it.slashdot.org/story/25/03/14/1351225/windows-defender-now-flags-winring0-driver-as-security-threat-breaking-multiple-pc-monitoring-tools
- AMD ADLX: https://gpuopen.com/adlx/ · repo: https://github.com/GPUOpen-LibrariesAndSDKs/ADLX
- Intel IGCL: https://github.com/intel/drivers.gpu.control-library · docs: https://intel.github.io/drivers.gpu.control-library/
- Intel PCM: https://github.com/intel/pcm · Power Gadget deprecado: https://github.com/mlco2/codecarbon/issues/457
- PawnIO (reemplazo WinRing0): https://poorlydocumented.com/2025/09/replacing-winring0-in-fan-control-with-pawnio/ · CapFrameX: https://github.com/CXWorld/CapFrameX
- NVIDIA NVML: https://developer.nvidia.com/management-library-nvml
- Firma código OSS: https://signpath.io/ · https://learn.microsoft.com/azure/trusted-signing/
- Monetización OSS: https://dev.to/thestackdeveloper01/50-real-ways-developers-can-earn-money-from-open-source-with-links-practical-tips-4k8o
