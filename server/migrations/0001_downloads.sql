-- How many times each release file was downloaded, per day. Nothing about who downloaded is kept.
--   kind    installer | portable | other
--   source  site (a person, from the website or a link) | update (LiteShot updating itself) | bot
CREATE TABLE downloads (
  day     TEXT    NOT NULL,              -- UTC date, YYYY-MM-DD
  kind    TEXT    NOT NULL,
  source  TEXT    NOT NULL,
  version TEXT    NOT NULL,
  count   INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (day, kind, source, version)
);
