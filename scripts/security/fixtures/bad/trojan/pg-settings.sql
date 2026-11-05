UPDATE pg_settings SET setting = 'SJIS' WHERE name = 'client_' || 'encoding';
