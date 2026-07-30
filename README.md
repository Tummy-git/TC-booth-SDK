# TummyCon Booth SDK

Welcome to the **TummyCon Booth SDK**! This toolkit is designed to help creators easily build, test, and export their virtual convention booths for the event. 

---

## 📂 1. Mandatory Folder Structure

To ensure your booth is packaged and accepted correctly by the server, **all your booth assets must be placed strictly inside a dedicated folder** matching your name:

> `Assets/Booth/[YourUsername]/`

*   **Rule:** If any asset (models, materials, textures, shaders) required by your booth is located outside this folder, the SDK exporter will **reject the export** and flag an error.
*   **Excluded Assets:** System folders like `TextMesh Pro`, `SerializedUdonPrograms`, and `Mochie` are automatically filtered out.

---

## 🚀 2. Getting Started & Setup

1. **Install VPM Package:** [Click this link to get to the listing. https://tummy-git.github.io/TC-booth-SDK/](https://tummy-git.github.io/TC-booth-SDK/)
2. **Open the Exporter:** Navigate to the top menu and select **Booth SDK > Open Booth Exporter**.
3. **Spawn the Reference Area:** Click **"Spawn Booth Reference Area"** in the Exporter window. This drops the `ConBoothArea` prefab into your scene to give you an accurate physical boundary and scale reference for your booth. *(Note: Make sure this reference object is **not** part of your final booth hierarchy when exporting!)*

---

## 📦 3. Preparing Your Booth

1. Create an active GameObject in your scene for your booth.
2. Attach the **`BoothDescriptor`** component to it.
3. Fill in your **VRChat Username** and the **Booth Name** in the component fields.
4. Organize all your booth's models, colliders, animations, materials, and scripts as children of this GameObject.

### ⚠️ Important Restrictions
* **No Scene Descriptors:** You are **not** allowed to place a `VRCSceneDescriptor` component inside your booth. Doing so will conflict with the Master Project settings and trigger a component violation error.
* **Shaders & Includes:** Custom shaders and `.cginc` include files are fully supported. The SDK automatically traces recursive shader dependencies so you don't have to worry about missing include files.

---

## 🛠️ 4. Exporting and Uploading

When you are ready to submit your booth:

1. Open the **Booth Exporter** window (**Booth SDK > Open Booth Exporter**).
2. Enter your assigned **Server Username** and **Password** (these are saved locally via `EditorPrefs` for convenience).
3. Ensure **"Clean unused ghost properties from materials"** is checked (Recommended). This automatically strips out invisible junk data left behind when changing shaders, keeping your file size small.
4. Click **"Build and Export Booth"**. 

### What happens when you click export?
*   It generates a clean local prefab of your booth.
*   It validates that all dependencies live inside your designated `Assets/Booth/[YourUsername]/` folder.
*   It packages your assets into a local `.unitypackage` and saves a backup copy locally in the `BoothExports/` folder.
*   It performs a size pre-check against the server limits.
*   It securely uploads your booth package directly to the event server.
