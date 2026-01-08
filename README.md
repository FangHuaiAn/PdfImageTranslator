# PdfImageTranslator

C# Console App，將 PDF 轉為純文字；若 PDF 為掃描影像型（無可用文字層），則將每頁渲染成影像後呼叫 OpenAI 視覺模型逐字轉錄成文字，再按頁序合併輸出。

## 功能特色

- **智慧文字抽取**：優先使用 PDF 文字層，節省 API 呼叫
- **OCR 備援**：掃描型 PDF 自動切換至 OpenAI Vision API
- **串流處理**：逐頁寫入，記憶體友善
- **可靠性**：HTTP 429/5xx 自動重試（指數退避 + jitter，最多 6 次）

## 環境需求

- .NET 8.0 SDK 或更新版本
- OpenAI API Key（僅 OCR 模式需要）

## 建置方式

```bash
cd src/PdfToTextOcr
dotnet restore
dotnet build -c Release
```

建置完成後，執行檔位於：
```
src/PdfToTextOcr/bin/Release/net8.0/PdfToTextOcr.exe    # Windows
src/PdfToTextOcr/bin/Release/net8.0/PdfToTextOcr        # Linux/macOS
```

## 環境變數

| 變數名稱 | 說明 | 必要性 |
|---------|------|--------|
| `OPENAI_API_KEY` | OpenAI API 金鑰 | OCR 模式必要 |

## 執行範例

### 基本用法

```bash
# 設定 API Key（僅 OCR 模式需要）
export OPENAI_API_KEY="sk-..."

# 轉換 PDF 為純文字
./PdfToTextOcr input.pdf output.txt
```

### Windows

```powershell
# 設定 API Key
$env:OPENAI_API_KEY = "sk-..."

# 執行轉換
.\PdfToTextOcr.exe document.pdf result.txt
```

## 輸出格式

輸出的 `.txt` 檔案以 UTF-8 編碼，每頁以分隔線標示：

```
=== Page 1 ===
這是第一頁的內容...

=== Page 2 ===
這是第二頁的內容...
```

## 運作邏輯

1. **文字層抽取**：使用 PdfPig 嘗試抽取 PDF 內嵌文字
2. **有效性判斷**：計算非空白字元數
   - 若 ≥ 200 字元：直接使用抽取結果
   - 若 < 200 字元：判定為掃描型，進入 OCR 流程
3. **OCR 流程**：
   - 使用 Docnet.Core 將每頁渲染為 ~300 DPI 的 PNG 影像
   - 呼叫 OpenAI Vision API (`gpt-4.1-mini`) 進行文字辨識
   - 逐頁串流寫入輸出檔

## 依賴套件

- **PdfPig** - PDF 文字層抽取
- **Docnet.Core** - PDF 頁面渲染為影像
- **System.Text.Json** - JSON 處理

## 授權

請參閱 [LICENSE](LICENSE) 檔案。
