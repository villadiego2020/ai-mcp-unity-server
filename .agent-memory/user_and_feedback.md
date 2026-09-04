# User and feedback

## CMM-001 AI Unity MCP Server configuration

- ผู้ใช้ต้องการให้โปรเจกต์ใหม่และ session ใหม่เชื่อม MCP ได้ง่าย โดยไม่ต้อง generate config ซ้ำ
- ต้องลดกรณี MCP active state ไม่ตรงและกรณีหา Unity tools ไม่เจอ
- ชื่อ user-facing ของระบบคือ `AI Unity MCP Server`
- ห้ามใช้ branded name/prefix ที่มีคำว่า `Delta` ทุกกรณี รวมถึง EditorPrefs, SessionState, Library paths, thread names, config และ docs
- ชื่อแสดงผลมาตรฐานคือ `AI Unity MCP Server`; identifier ที่เว้นวรรคไม่ได้ใช้ `AIUnityMCPServer`
- Repository content must be English-only: no Thai comments, strings, tests, manifests, README text, or documentation.
