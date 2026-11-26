BEGIN;

-- The pgvector extension must already be installed in this database.
CREATE TABLE IF NOT EXISTS cyclus_knowledge_documents (
	id text PRIMARY KEY,
	title text NOT NULL,
	category text NOT NULL,
	metadata jsonb NOT NULL,
	article jsonb,
	content_hash text NOT NULL,
	model text NOT NULL,
	indexed_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS cyclus_knowledge_chunks (
	document_id text NOT NULL REFERENCES cyclus_knowledge_documents(id) ON DELETE CASCADE,
	ordinal integer NOT NULL CHECK (ordinal > 0),
	body text NOT NULL,
	location text NOT NULL,
	codes text[] NOT NULL,
	embedding vector(1024) NOT NULL,
	PRIMARY KEY (document_id, ordinal)
);

COMMIT;
