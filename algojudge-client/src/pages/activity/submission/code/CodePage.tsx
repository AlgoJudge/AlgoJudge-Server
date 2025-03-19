import { Title } from "@mantine/core";
import classes from "./CodePage.module.css"
import { useTranslation } from "react-i18next";

interface Model {
    /* ... */
}

const data: Model = { /* ... */};

export default function CodePage() {
    const { t } = useTranslation();
    return (
        <>
            <Title>{t("Source code")}</Title>
        </>
    );
}
