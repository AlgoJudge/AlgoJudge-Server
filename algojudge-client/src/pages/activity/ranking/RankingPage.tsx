import { Title } from "@mantine/core";
import classes from "./RankingPage.module.css"
import { useTranslation } from "react-i18next";

interface Model {
    /* ... */
}

const data: Model = { /* ... */};

export default function RankingPage() {
    const { t } = useTranslation();
    return (
        <>
            <Title>{t("Ranking")}</Title>
        </>
    );
}
