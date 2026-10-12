-- TopupLedger MySQL 8 schema. InnoDB, utf8mb4. Money columns are BIGINT fils.
-- Run against an empty database: mysql topupledger < db/schema.sql

SET NAMES utf8mb4;

CREATE TABLE partners (
    id CHAR(36) NOT NULL,
    name VARCHAR(200) NOT NULL,
    tier VARCHAR(20) NOT NULL,
    status VARCHAR(20) NOT NULL,
    api_key VARCHAR(80) NOT NULL,
    secret_protected VARBINARY(512) NOT NULL,
    parent_partner_id CHAR(36) NULL,
    created_at_utc DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uk_partners_api_key (api_key),
    KEY ix_partners_parent (parent_partner_id),
    CONSTRAINT fk_partners_parent FOREIGN KEY (parent_partner_id) REFERENCES partners (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE accounts (
    id CHAR(36) NOT NULL,
    partner_id CHAR(36) NULL,
    account_type VARCHAR(32) NOT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uk_accounts_partner (partner_id),
    CONSTRAINT fk_accounts_partner FOREIGN KEY (partner_id) REFERENCES partners (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE wallets (
    partner_id CHAR(36) NOT NULL,
    account_id CHAR(36) NOT NULL,
    balance_fils BIGINT NOT NULL,
    PRIMARY KEY (partner_id),
    UNIQUE KEY uk_wallets_account (account_id),
    CONSTRAINT ck_wallets_balance_non_negative CHECK (balance_fils >= 0),
    CONSTRAINT fk_wallets_partner FOREIGN KEY (partner_id) REFERENCES partners (id),
    CONSTRAINT fk_wallets_account FOREIGN KEY (account_id) REFERENCES accounts (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE ledger_entries (
    id BIGINT NOT NULL AUTO_INCREMENT,
    transaction_id CHAR(36) NOT NULL,
    account_id CHAR(36) NOT NULL,
    amount_fils BIGINT NOT NULL,
    entry_type VARCHAR(40) NOT NULL,
    topup_id CHAR(36) NULL,
    correlation_id VARCHAR(64) NULL,
    reason VARCHAR(500) NULL,
    created_at_utc DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    KEY ix_ledger_tx (transaction_id),
    KEY ix_ledger_account_id (account_id, id),
    KEY ix_ledger_topup (topup_id),
    CONSTRAINT fk_ledger_account FOREIGN KEY (account_id) REFERENCES accounts (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE topups (
    id CHAR(36) NOT NULL,
    partner_id CHAR(36) NOT NULL,
    client_ref VARCHAR(64) NOT NULL,
    msisdn VARCHAR(15) NOT NULL,
    operator VARCHAR(20) NOT NULL,
    face_value_fils BIGINT NOT NULL,
    retailer_commission_fils BIGINT NOT NULL,
    distributor_share_fils BIGINT NOT NULL,
    debit_fils BIGINT NOT NULL,
    status VARCHAR(20) NOT NULL,
    business_day DATE NOT NULL,
    idempotency_key CHAR(36) NOT NULL,
    highest_event_seq INT NOT NULL DEFAULT 0,
    operator_txn_id VARCHAR(64) NULL,
    created_at_utc DATETIME(6) NOT NULL,
    completed_at_utc DATETIME(6) NULL,
    dispatched_at_utc DATETIME(6) NULL,
    correlation_id VARCHAR(64) NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uk_topups_partner_client (partner_id, client_ref),
    UNIQUE KEY uk_topups_partner_idem (partner_id, idempotency_key),
    KEY ix_topups_partner_created (partner_id, created_at_utc, id),
    KEY ix_topups_msisdn_created (msisdn, created_at_utc, id),
    KEY ix_topups_status_created (status, created_at_utc, id),
    KEY ix_topups_business_day (business_day, partner_id),
    KEY ix_topups_operator_txn (operator_txn_id),
    KEY ix_topups_dispatch (status, dispatched_at_utc, created_at_utc),
    CONSTRAINT fk_topups_partner FOREIGN KEY (partner_id) REFERENCES partners (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE idempotency_records (
    partner_id CHAR(36) NOT NULL,
    idempotency_key CHAR(36) NOT NULL,
    request_hash CHAR(64) NOT NULL,
    state VARCHAR(20) NOT NULL,
    http_status SMALLINT NULL,
    response_body MEDIUMBLOB NULL,
    content_type VARCHAR(80) NULL,
    location VARCHAR(200) NULL,
    locked_until_utc DATETIME(6) NOT NULL,
    created_at_utc DATETIME(6) NOT NULL,
    completed_at_utc DATETIME(6) NULL,
    PRIMARY KEY (partner_id, idempotency_key),
    KEY ix_idem_created (created_at_utc),
    CONSTRAINT fk_idem_partner FOREIGN KEY (partner_id) REFERENCES partners (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE operator_callbacks (
    id BIGINT NOT NULL AUTO_INCREMENT,
    topup_id CHAR(36) NOT NULL,
    event_seq INT NOT NULL,
    status VARCHAR(20) NOT NULL,
    operator_txn_id VARCHAR(64) NOT NULL,
    applied TINYINT(1) NOT NULL,
    received_at_utc DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uk_callback_event (topup_id, event_seq),
    CONSTRAINT fk_callback_topup FOREIGN KEY (topup_id) REFERENCES topups (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE late_callbacks (
    id BIGINT NOT NULL AUTO_INCREMENT,
    topup_id CHAR(36) NOT NULL,
    event_seq INT NOT NULL,
    status VARCHAR(20) NOT NULL,
    operator_txn_id VARCHAR(64) NOT NULL,
    received_at_utc DATETIME(6) NOT NULL,
    note VARCHAR(200) NOT NULL,
    PRIMARY KEY (id),
    KEY ix_late_topup (topup_id, event_seq),
    CONSTRAINT fk_late_topup FOREIGN KEY (topup_id) REFERENCES topups (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE rate_buckets (
    partner_id CHAR(36) NOT NULL,
    tokens INT NOT NULL,
    last_refill_utc DATETIME(6) NOT NULL,
    PRIMARY KEY (partner_id),
    CONSTRAINT fk_rate_partner FOREIGN KEY (partner_id) REFERENCES partners (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE settlement_batches (
    id CHAR(36) NOT NULL,
    business_day DATE NOT NULL,
    status VARCHAR(20) NOT NULL,
    created_at_utc DATETIME(6) NOT NULL,
    completed_at_utc DATETIME(6) NULL,
    error VARCHAR(500) NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uk_settlement_day (business_day)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE settlement_lines (
    id BIGINT NOT NULL AUTO_INCREMENT,
    batch_id CHAR(36) NOT NULL,
    partner_id CHAR(36) NOT NULL,
    sales_count INT NOT NULL,
    face_value_fils BIGINT NOT NULL,
    retailer_commission_fils BIGINT NOT NULL,
    distributor_share_fils BIGINT NOT NULL,
    debit_fils BIGINT NOT NULL,
    parent_distributor_income_fils BIGINT NOT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uk_settlement_line (batch_id, partner_id),
    CONSTRAINT fk_settlement_line_batch FOREIGN KEY (batch_id) REFERENCES settlement_batches (id),
    CONSTRAINT fk_settlement_line_partner FOREIGN KEY (partner_id) REFERENCES partners (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE reconciliation_runs (
    id CHAR(36) NOT NULL,
    file_sha256 CHAR(64) NOT NULL,
    file_name VARCHAR(260) NOT NULL,
    status VARCHAR(20) NOT NULL,
    created_at_utc DATETIME(6) NOT NULL,
    matched_count INT NOT NULL DEFAULT 0,
    missing_in_ours_count INT NOT NULL DEFAULT 0,
    missing_in_operator_count INT NOT NULL DEFAULT 0,
    amount_mismatch_count INT NOT NULL DEFAULT 0,
    status_mismatch_count INT NOT NULL DEFAULT 0,
    duplicate_row_count INT NOT NULL DEFAULT 0,
    PRIMARY KEY (id),
    UNIQUE KEY uk_recon_hash (file_sha256)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE reconciliation_lines (
    id BIGINT NOT NULL AUTO_INCREMENT,
    run_id CHAR(36) NOT NULL,
    operator_txn_id VARCHAR(64) NOT NULL,
    msisdn VARCHAR(20) NOT NULL,
    amount_fils BIGINT NOT NULL,
    status VARCHAR(20) NOT NULL,
    event_time_dubai DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uk_recon_line (run_id, operator_txn_id),
    CONSTRAINT fk_recon_line_run FOREIGN KEY (run_id) REFERENCES reconciliation_runs (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE reconciliation_findings (
    id BIGINT NOT NULL AUTO_INCREMENT,
    run_id CHAR(36) NOT NULL,
    category VARCHAR(32) NOT NULL,
    operator_txn_id VARCHAR(64) NULL,
    topup_id CHAR(36) NULL,
    detail VARCHAR(500) NOT NULL,
    PRIMARY KEY (id),
    KEY ix_findings_run (run_id, category),
    CONSTRAINT fk_findings_run FOREIGN KEY (run_id) REFERENCES reconciliation_runs (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE portal_users (
    id CHAR(36) NOT NULL,
    username VARCHAR(80) NOT NULL,
    password_hash VARBINARY(64) NOT NULL,
    password_salt VARBINARY(32) NOT NULL,
    role VARCHAR(20) NOT NULL,
    created_at_utc DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uk_portal_username (username)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

DELIMITER $$
CREATE TRIGGER trg_ledger_no_update
BEFORE UPDATE ON ledger_entries
FOR EACH ROW
BEGIN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ledger entries are immutable';
END$$
CREATE TRIGGER trg_ledger_no_delete
BEFORE DELETE ON ledger_entries
FOR EACH ROW
BEGIN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ledger entries are immutable';
END$$
DELIMITER ;
