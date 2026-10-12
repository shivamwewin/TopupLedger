-- Must return no rows. Wallet balance equals the sum of its ledger entries,
-- every movement sums to zero, and the whole book sums to zero.

SELECT transaction_id, SUM(amount_fils) AS movement_sum
FROM ledger_entries
GROUP BY transaction_id
HAVING SUM(amount_fils) <> 0;

SELECT w.partner_id, w.balance_fils, COALESCE(SUM(e.amount_fils), 0) AS ledger_sum
FROM wallets w
LEFT JOIN ledger_entries e ON e.account_id = w.account_id
GROUP BY w.partner_id, w.balance_fils
HAVING w.balance_fils <> COALESCE(SUM(e.amount_fils), 0);

SELECT SUM(amount_fils) AS book_sum
FROM ledger_entries
HAVING SUM(amount_fils) <> 0;
