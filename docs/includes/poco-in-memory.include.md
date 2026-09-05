The registered sequence is wrapped with `AsQueryable()`, so the pipeline runs in memory over LINQ to
Objects with the same validation, shaping, and limits as a database source. The string functions run
ordinally there — a prefix, a suffix, a search, a casing, a three-way comparison — so an answer does not
follow the request's culture, as a database source's answer does not.
