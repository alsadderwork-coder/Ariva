/* outer /* inner */ still a comment */
SELECT 'a--b', '/* not a comment */', $x$ -- $x$, E'a line\n', E'a \\ backslash';
SET client_encoding = 'UTF8'; -- a trailing comment
