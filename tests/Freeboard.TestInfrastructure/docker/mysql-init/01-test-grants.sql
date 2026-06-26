-- Local dev/test convenience only. Lets the freeboard user create and use the
-- throwaway `fb_test_*` databases the integration test suite provisions per test.
-- This grant is for the local docker-compose MySQL only; do not replicate it in a
-- real deployment, where the runtime user needs no database-creation rights.
GRANT ALL PRIVILEGES ON `fb\_test\_%`.* TO 'freeboard'@'%';
GRANT CREATE ON *.* TO 'freeboard'@'%';
FLUSH PRIVILEGES;
