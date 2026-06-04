# 参考资料说明

## 官方 Openness 手册 PDF —— 为什么不在这里

官方手册《TIA Portal Openness:用于工程组态工作流自动化的 API》(中文,11/2023 版,1930 页)
下载地址:
https://cache.industry.siemens.com/dl/files/886/109826886/att_1163885/v1/TIAPortalOpennesszhCN_zh-CHS.pdf

**这个 PDF 无法持久存放在本项目目录**:本机 E-SafeNet 透明加密会按"定期全盘扫描"把落盘明文重新加密
(实测:终端下载那刻是明文 `%PDF-1.5`,几分钟后再读已变成密文 `62 14 23 65 65…`,pypdf 直接报
"invalid pdf header")。所以项目目录里的 PDF 副本已删除。

## 怎么用它

- **要点已抽取进 spec**:`docs/superpowers/specs/2026-06-03-hmi-probe-design.md`
  (设备支持矩阵表 5-1~5-7、HMI 对象模型 API、画面导出/导入签名,均含手册页码)。
- **需要再查时**:用终端把它下到 `%TEMP%`(E-SafeNet 豁免区,明文不会被加密),再用 Python+pypdf 抽页:
  ```powershell
  Invoke-WebRequest -Uri <上面的URL> -OutFile "$env:TEMP\tia_openness.pdf" -UseBasicParsing -UserAgent "Mozilla/5.0"
  python -m pip install --user pypdf
  # 写个小脚本 PdfReader($env:TEMP\tia_openness.pdf).pages[i].extract_text()
  ```
  (Read 工具读 PDF 依赖 poppler 的 `pdftoppm`,本机未装,故走 pypdf 抽文本。)
