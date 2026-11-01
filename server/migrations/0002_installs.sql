-- Copies of LiteShot in use. A copy's first update check of the day carries a random install id (made once, on the
-- PC); only a SHA-256 of it is stored, with the version and channel. Nothing else about the PC or the person is kept.
CREATE TABLE installs (
  install   TEXT NOT NULL PRIMARY KEY,   -- hex SHA-256 of the install id
  first_day TEXT NOT NULL,               -- UTC date it was first counted, YYYY-MM-DD
  last_day  TEXT NOT NULL,
  version   TEXT NOT NULL,               -- the version it last ran
  channel   TEXT NOT NULL
);
CREATE INDEX installs_last_day ON installs (last_day);

-- Which copies checked in on each day, for daily and monthly active counts. Rows older than 400 days are removed.
CREATE TABLE active (
  day     TEXT NOT NULL,
  install TEXT NOT NULL,
  version TEXT NOT NULL,
  PRIMARY KEY (day, install)
);
