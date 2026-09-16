-- Run once as the postgres superuser:  psql -U postgres -f create-db-role.sql
-- Creates a dedicated, least-privilege role for the app: it can only use the orbit database.

CREATE ROLE orbit WITH LOGIN PASSWORD 'CHANGE_ME' NOSUPERUSER NOCREATEDB NOCREATEROLE;
CREATE DATABASE orbit OWNER orbit;

-- Lock the database down to the orbit role only.
REVOKE ALL ON DATABASE orbit FROM PUBLIC;
GRANT CONNECT, TEMP ON DATABASE orbit TO orbit;

\connect orbit
-- The orbit role owns the public schema so EF migrations can create tables/indexes.
ALTER SCHEMA public OWNER TO orbit;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
