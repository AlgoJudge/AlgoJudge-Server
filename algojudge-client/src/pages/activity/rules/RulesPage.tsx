import { Title } from "@mantine/core";
import classes from "./RulesPage.module.css"
import { useTranslation } from "react-i18next";

interface Model {
    /* ... */
}

const data: Model = { /* ... */};

export default function RulesPage() {
    const { t } = useTranslation();
    return (
        <>
            <Title>{t("Rules")}</Title>
        </>
    );
}
