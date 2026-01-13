# PdfImageTranslator 運作邏輯說明

## 1. 專案概述 (Project Overview)
本專案為 C# Console Application，旨在將 PDF 文件轉換為純文字檔案。它具備混合處理能力，能針對不同類型的 PDF 頁面採取最適當的提取策略。

- **核心功能**: PDF 轉純文字 (PDF to Plain Text)
- **支援類型**: 
  - 文字型 PDF (Text-based): 直接提取文字。
  - 掃描/影像型 PDF (Image-based): 透過 OCR (OpenAI Vision) 轉錄。

## 2. 系統依賴 (Dependencies)
- **UglyToad.PdfPig**: 用於解析 PDF 結構與提取文字層。
- **PDFtoImage (SkiaSharp)**: 用於將 PDF 頁面渲染為圖片。
- **OpenAI API**: 使用 GPT-4 Vision 模型進行光學字元識別 (OCR)。
- **Microsoft.Extensions.Configuration**: 處理設定檔與環境變數。

## 3. 詳細運作流程 (Detailed Flow)

### 3.1. 初始化階段 (Initialization)
1.  **載入設定**: 
    - 讀取 `appsettings.json` 或環境變數 `OPENAI_API_KEY` 以取得 OpenAI API 金鑰。
    - 讀取 `InputPath` (輸入資料夾) 與 `OutputPath` (輸出資料夾) 設定。
2.  **環境準備**:
    - 驗證輸入資料夾是否存在。
    - 若輸出資料夾不存在，則自動建立。
    - 取得輸入資料夾內所有 PDF 檔案列表 (`*.pdf`)。

### 3.2. 處理階段 (Processing Loop)
程式針對列表中的每一個 PDF 檔案進行批次處理：

1.  **檔案層級**: 開啟 PDF 檔案。
2.  **頁面層級**: 針對每一頁 (Page) 執行以下邏輯：
    1.  **頁面分析**: 檢查該頁面是否包含可提取的文字層。
    2.  **分支處理**:
    *   **路徑 A：文字型頁面 (Text Layer Exists)**
        *   呼叫 `PdfPig` 函式庫。
        *   直接從 PDF 結構中提取文字內容。
    *   **路徑 B：影像型頁面 (No Text Layer)**
        *   **渲染**: 使用 `PDFtoImage` 將該頁面轉換為高解析度圖片。
        *   **OCR 識別**: 將圖片發送至 OpenAI GPT-4 Vision API。
        *   **轉錄**: 接收 AI 回傳的圖片文字描述。
    3.  **收集結果**: 將提取或轉錄的文字暫存，並標記頁碼 (e.g., `--- Page N ---`)。

### 3.3. 輸出階段 (Output)
1.  **合併內容**: 依照頁面順序將所有文字組合成單一字串。
2.  **寫入檔案**: 將結果儲存至輸出資料夾，檔名對應原 PDF 檔名 (例如 `doc.pdf` -> `doc.txt`)。

## 4. 限制與注意事項
- OCR 功能依賴有效的 OpenAI API Key。
- 處理掃描型 PDF 時，速度取決於 API 回應時間與圖片複雜度。