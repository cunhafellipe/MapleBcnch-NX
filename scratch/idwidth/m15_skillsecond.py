import nx
print("Second half of the database.js skill defect: the ENTRY key, independent of the book.")
rows = [("0000008", 8), ("0000012", 12), ("0000100", 100), ("0001000", 1000), ("0009002", 9002)]
for key, sid in rows:
    book_js = "%d.img" % (sid // 10000)          # database.js: floor(id/10000)
    print("  stored key f8/000.img/skill/%s  (id %d)" % (key, sid))
    print("     database.js book candidates : %r, %r  -> exist: %s / %s"
          % (book_js, str(sid // 10000).rjust(4, '0') + ".img",
             nx.exists("f8/" + book_js), nx.exists("f8/" + str(sid // 10000).rjust(4, '0') + ".img")))
    print("     C++ book (extend_id 3)       : %r -> exists: %s"
          % ("000.img", nx.exists("f8/000.img")))
    print("     entry under the RIGHT book, unpadded %-8s -> %s | D7 %-8s -> %s"
          % (str(sid), nx.exists("f8/000.img/skill/%d" % sid),
             key, nx.exists("f8/000.img/skill/%s" % key)))