import { Button, Group, Title } from "@mantine/core";
import classes from "./CodePage.module.css"
import { useTranslation } from "react-i18next";
import { useNavigate } from "react-router-dom";

interface Model {
    /* ... */
}

const data: Model = { /* ... */ };

export default function CodePage() {
    const { t } = useTranslation();
    const navigate = useNavigate();
    return (
        <>
            <Title>{t("Source code")}</Title>
            <Group>
                <Button onClick={() => navigate(-1)}>{t("Back")}</Button>
            </Group>
        </>
    );
}
