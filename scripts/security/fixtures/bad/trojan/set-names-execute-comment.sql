DO $$ BEGIN EXECUTE 'SELECT ''a--b''; SET/**/NAMES ''SJIS'''; END $$;
