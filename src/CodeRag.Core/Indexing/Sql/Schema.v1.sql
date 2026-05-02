CREATE TABLE index_metadata (
    metadata_id                    INTEGER PRIMARY KEY CHECK (metadata_id = 1),
    schema_version                 INTEGER NOT NULL,
    solution_file_path             TEXT NOT NULL,
    repository_root_path           TEXT NOT NULL,
    indexed_at_commit_sha          TEXT NOT NULL,
    indexed_at_utc                 TEXT NOT NULL,
    embedding_model_name           TEXT NOT NULL,
    embedding_vector_dimensions    INTEGER NOT NULL
);

CREATE TABLE code_chunks (
    chunk_id                              INTEGER PRIMARY KEY,

    containing_project_name               TEXT NOT NULL,
    containing_assembly_name              TEXT NOT NULL,
    relative_file_path                    TEXT NOT NULL,
    start_line_number                     INTEGER NOT NULL,
    end_line_number                       INTEGER NOT NULL,

    symbol_kind                           TEXT NOT NULL,
    symbol_display_name                   TEXT NOT NULL,
    symbol_signature_display              TEXT NOT NULL,
    fully_qualified_symbol_name           TEXT NOT NULL,
    containing_namespace                  TEXT NULL,
    parent_symbol_fully_qualified_name    TEXT NULL,

    accessibility                         TEXT NOT NULL,
    is_static                             INTEGER NOT NULL DEFAULT 0,
    is_abstract                           INTEGER NOT NULL DEFAULT 0,
    is_sealed                             INTEGER NOT NULL DEFAULT 0,
    is_virtual                            INTEGER NOT NULL DEFAULT 0,
    is_override                           INTEGER NOT NULL DEFAULT 0,
    is_async                              INTEGER NOT NULL DEFAULT 0,
    is_partial                            INTEGER NOT NULL DEFAULT 0,
    is_readonly                           INTEGER NOT NULL DEFAULT 0,
    is_extern                             INTEGER NOT NULL DEFAULT 0,
    is_unsafe                             INTEGER NOT NULL DEFAULT 0,
    is_extension_method                   INTEGER NOT NULL DEFAULT 0,
    is_generic                            INTEGER NOT NULL DEFAULT 0,

    base_type_fully_qualified_name        TEXT NULL,

    return_type_fully_qualified_name      TEXT NULL,
    parameter_count                       INTEGER NULL,

    documentation_comment_xml             TEXT NULL,

    source_text                           TEXT NOT NULL,
    source_text_hash                      TEXT NOT NULL
);

CREATE TABLE chunk_attributes (
    attribute_id                          INTEGER PRIMARY KEY,
    chunk_id                              INTEGER NOT NULL REFERENCES code_chunks(chunk_id) ON DELETE CASCADE,
    attribute_fully_qualified_name        TEXT NOT NULL,
    attribute_arguments_json              TEXT NULL
);

CREATE TABLE chunk_implemented_interfaces (
    chunk_id                              INTEGER NOT NULL REFERENCES code_chunks(chunk_id) ON DELETE CASCADE,
    interface_fully_qualified_name        TEXT NOT NULL,
    PRIMARY KEY (chunk_id, interface_fully_qualified_name)
);

CREATE TABLE chunk_method_parameters (
    chunk_id                              INTEGER NOT NULL REFERENCES code_chunks(chunk_id) ON DELETE CASCADE,
    parameter_ordinal                     INTEGER NOT NULL,
    parameter_name                        TEXT NOT NULL,
    parameter_type_fully_qualified_name   TEXT NOT NULL,
    parameter_modifier                    TEXT NULL,
    has_default_value                     INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (chunk_id, parameter_ordinal)
);

CREATE TABLE chunk_generic_type_parameters (
    chunk_id                              INTEGER NOT NULL REFERENCES code_chunks(chunk_id) ON DELETE CASCADE,
    parameter_ordinal                     INTEGER NOT NULL,
    parameter_name                        TEXT NOT NULL,
    constraints_json                      TEXT NULL,
    PRIMARY KEY (chunk_id, parameter_ordinal)
);

CREATE INDEX idx_chunks_file          ON code_chunks(relative_file_path);
CREATE INDEX idx_chunks_kind          ON code_chunks(symbol_kind);
CREATE INDEX idx_chunks_project       ON code_chunks(containing_project_name);
CREATE INDEX idx_chunks_namespace     ON code_chunks(containing_namespace);
CREATE INDEX idx_chunks_fqn           ON code_chunks(fully_qualified_symbol_name);
CREATE INDEX idx_chunks_parent        ON code_chunks(parent_symbol_fully_qualified_name);
CREATE INDEX idx_chunks_accessibility ON code_chunks(accessibility);
CREATE INDEX idx_chunks_return_type   ON code_chunks(return_type_fully_qualified_name);
CREATE INDEX idx_chunks_base_type     ON code_chunks(base_type_fully_qualified_name);
CREATE INDEX idx_attributes_name      ON chunk_attributes(attribute_fully_qualified_name);
CREATE INDEX idx_interfaces_name      ON chunk_implemented_interfaces(interface_fully_qualified_name);
CREATE INDEX idx_params_type          ON chunk_method_parameters(parameter_type_fully_qualified_name);

CREATE VIRTUAL TABLE chunk_embeddings USING vec0(embedding float[3072]);
