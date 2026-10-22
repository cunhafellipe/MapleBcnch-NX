import nx, json

j = nx._get("/api/files", {})
print("RAW /api/files type:", type(j).__name__)
print(json.dumps(j, ensure_ascii=False)[:2000])
print("---- derived ----")
print(json.dumps(nx.files(), ensure_ascii=False, indent=1))